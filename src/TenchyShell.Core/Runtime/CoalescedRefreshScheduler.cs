namespace TenchyShell.Core.Runtime;

/// <summary>
/// Programa refrescos guiados por eventos sin acumular trabajo repetido.
/// Mientras un refresco se ejecuta sólo conserva una pasada adicional.
/// </summary>
public sealed class CoalescedRefreshScheduler : IDisposable
{
    private readonly object gate = new();
    private readonly Func<Action, bool> post;
    private readonly Action refresh;
    private readonly Action<Exception> onError;
    private readonly Action? onRefreshStarting;
    private readonly bool collectRequestCounts;
    private bool pending;
    private bool refreshing;
    private bool refreshAgain;
    private bool disposed;
    private long requests;
    private long coalescedRequests;

    public CoalescedRefreshScheduler(Func<Action, bool> post, Action refresh, Action<Exception> onError,
        Action? onRefreshStarting = null, bool collectRequestCounts = true)
    {
        this.post = post ?? throw new ArgumentNullException(nameof(post));
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.onError = onError ?? throw new ArgumentNullException(nameof(onError));
        this.onRefreshStarting = onRefreshStarting;
        this.collectRequestCounts = collectRequestCounts;
    }

    public bool Request(Action<bool, bool>? observeRequest = null)
    {
        if (collectRequestCounts) Interlocked.Increment(ref requests);
        lock (gate)
        {
            if (disposed)
            {
                observeRequest?.Invoke(false, false);
                return false;
            }
            if (refreshing)
            {
                if (collectRequestCounts) Interlocked.Increment(ref coalescedRequests);
                observeRequest?.Invoke(true, true);
                refreshAgain = true;
                return true;
            }

            if (pending)
            {
                if (collectRequestCounts) Interlocked.Increment(ref coalescedRequests);
                observeRequest?.Invoke(true, true);
                return true;
            }
            pending = true;
            var accepted = post(Execute);
            observeRequest?.Invoke(accepted, false);
            if (accepted) return true;

            pending = false;
            return false;
        }
    }

    public long Requests => Interlocked.Read(ref requests);

    public long CoalescedRequests => Interlocked.Read(ref coalescedRequests);

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending = false;
            refreshAgain = false;
        }
    }

    private void Execute()
    {
        lock (gate)
        {
            pending = false;
            if (disposed) return;
            refreshing = true;
            onRefreshStarting?.Invoke();
        }

        try { refresh(); }
        catch (Exception exception) { onError(exception); }
        finally
        {
            lock (gate)
            {
                refreshing = false;
                if (!disposed && refreshAgain)
                {
                    refreshAgain = false;
                    pending = true;
                    if (!post(Execute)) pending = false;
                }
            }
        }
    }
}
