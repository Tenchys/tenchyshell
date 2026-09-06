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
    private bool pending;
    private bool refreshing;
    private bool refreshAgain;
    private bool disposed;

    public CoalescedRefreshScheduler(Func<Action, bool> post, Action refresh, Action<Exception> onError)
    {
        this.post = post ?? throw new ArgumentNullException(nameof(post));
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    public bool Request()
    {
        lock (gate)
        {
            if (disposed) return false;
            if (refreshing)
            {
                refreshAgain = true;
                return true;
            }

            if (pending) return true;
            pending = true;
            if (post(Execute)) return true;

            pending = false;
            return false;
        }
    }

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
