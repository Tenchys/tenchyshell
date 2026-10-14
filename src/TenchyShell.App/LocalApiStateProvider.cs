using System.Diagnostics;
using TenchyShell.Core.Applications;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.LocalApi;
using TenchyShell.Core.Windows;
using TenchyShell.Win32;
using TenchyShell.Workspaces;

namespace TenchyShell.App;

internal sealed class LocalApiStateProvider : ILocalApiStateProvider, IDisposable
{
    private readonly MessageLoopHost messageLoop;
    private readonly WorkspaceManager workspaces;
    private readonly IWorkspaceWindowService windowService;
    private readonly IRefreshableApplicationCatalog applications;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly object topicGate = new();
    private readonly HashSet<string> interestedTopics = new(StringComparer.Ordinal);

    public LocalApiStateProvider(
        MessageLoopHost messageLoop,
        WorkspaceManager workspaces,
        IWorkspaceWindowService windowService,
        IRefreshableApplicationCatalog applications,
        LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.messageLoop = messageLoop;
        this.workspaces = workspaces;
        this.windowService = windowService;
        this.applications = applications;
        this.benchmarkRecorder = benchmarkRecorder;
        workspaces.StateChanged += OnWorkspaceStateChanged;
    }

    public event EventHandler<LocalApiStateChangedEventArgs>? StateChanged;

    public LocalApiSnapshot GetSnapshot() => OnLoop(BuildSnapshot);

    public LocalApiResult SwitchWorkspace(int workspace)
    {
        var result = OnLoop(() => workspaces.SwitchTo(workspace));
        return result.Succeeded
            ? LocalApiResult.Success()
            : LocalApiResult.Failure("invalid_workspace", result.Error ?? "No se pudo cambiar el workspace.");
    }

    public LocalApiResult MoveActiveWindow(int workspace)
    {
        var result = OnLoop(() => workspaces.MoveForegroundTo(workspace));
        return result.Succeeded
            ? LocalApiResult.Success()
            : LocalApiResult.Failure(
                result.Error?.Contains("ventana activa", StringComparison.OrdinalIgnoreCase) == true
                    ? "no_foreground_window"
                    : "window_operation_rejected",
                result.Error ?? "No se pudo mover la ventana activa.");
    }

    public void SetTopicInterest(string topic, bool interested)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        lock (topicGate)
        {
            if (interested) interestedTopics.Add(topic);
            else interestedTopics.Remove(topic);
        }
    }

    public void RefreshWindows() => OnLoop(() =>
    {
        lock (topicGate)
        {
            if (!interestedTopics.Contains("windows")) return 0;
        }
        workspaces.Refresh();
        Publish("windows");
        return 0;
    });

    public void RefreshApplications() => OnLoop(() =>
    {
        applications.Refresh("watcher");
        Publish("applications");
        return 0;
    });

    public void Dispose() => workspaces.StateChanged -= OnWorkspaceStateChanged;

    private void OnWorkspaceStateChanged(object? sender, EventArgs args) =>
        Publish("workspace");

    private void Publish(string topic) =>
        StateChanged?.Invoke(this, new LocalApiStateChangedEventArgs(topic));

    private LocalApiSnapshot BuildSnapshot()
    {
        var operation = workspaces.CaptureCurrentWorkspaceSnapshot("local_api");
        var allWindows = operation.WindowsByWorkspace.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<IntPtr>)pair.Value.Select(snapshot => snapshot.Handle).ToArray());
        var windows = new List<LocalApiWindow>();
        foreach (var pair in operation.WindowsByWorkspace.OrderBy(pair => pair.Key))
        {
            foreach (var snapshot in pair.Value.OrderBy(value => value.Handle.ToInt64()))
            {
                try
                {
                    if (!snapshot.Exists || string.IsNullOrWhiteSpace(snapshot.Title)) continue;
                    windows.Add(new LocalApiWindow(
                        $"0x{snapshot.Handle.ToInt64():X}",
                        pair.Key,
                        snapshot.Title,
                        snapshot.IsVisible,
                        snapshot.IsMinimized,
                        snapshot.IsOperable));
                }
                catch
                {
                    // Un HWND puede desaparecer durante la enumeración.
                }
            }
        }

        var workspaceList = Enumerable.Range(WorkspaceManager.FirstWorkspace, WorkspaceManager.LastWorkspace)
            .Select(id => new LocalApiWorkspace(id, allWindows.TryGetValue(id, out var values) ? values.Count : 0))
            .ToArray();
        var applicationList = applications.GetAll()
            .Where(application => !string.IsNullOrWhiteSpace(application.DisplayName))
            .OrderBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(application => new LocalApiApplication(
                application.DisplayName,
                application.ActivationKind.ToString(),
                application.AppUserModelId))
            .ToArray();

        return new LocalApiSnapshot(workspaces.CurrentWorkspace, workspaceList, windows, applicationList);
    }

    private T OnLoop<T>(Func<T> action)
    {
        if (messageLoop.IsOnMessageLoopThread) return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedAt = Stopwatch.GetTimestamp();
        if (!messageLoop.TryPost(() =>
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                completion.SetResult(action());
                benchmarkRecorder?.RecordAtMostEverySampleInterval("local_api_message_loop_work", new
                {
                    queueLatencyMs = Stopwatch.GetElapsedTime(queuedAt, startedAt).TotalMilliseconds,
                    durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    pendingActions = messageLoop.PendingActionCount,
                    maximumPendingActions = messageLoop.MaximumPendingActionCount
                });
            }
            catch (Exception exception) { completion.SetException(exception); }
        }))
        {
            throw new InvalidOperationException("El message loop no acepta trabajo para la API local.");
        }

        return completion.Task.WaitAsync(TimeSpan.FromMilliseconds(LocalApiDefaults.OperationTimeoutMilliseconds)).GetAwaiter().GetResult();
    }
}
