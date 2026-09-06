using TenchyShell.Core.Applications;
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
    private readonly WindowsApplicationCatalog applications;

    public LocalApiStateProvider(
        MessageLoopHost messageLoop,
        WorkspaceManager workspaces,
        IWorkspaceWindowService windowService,
        WindowsApplicationCatalog applications)
    {
        this.messageLoop = messageLoop;
        this.workspaces = workspaces;
        this.windowService = windowService;
        this.applications = applications;
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

    public void RefreshWindows() => OnLoop(() =>
    {
        workspaces.Refresh();
        Publish("windows");
        return 0;
    });

    public void RefreshApplications() => OnLoop(() =>
    {
        applications.Refresh();
        Publish("applications");
        return 0;
    });

    public void Dispose() => workspaces.StateChanged -= OnWorkspaceStateChanged;

    private void OnWorkspaceStateChanged(object? sender, EventArgs args) =>
        Publish("workspace");

    private void Publish(string topic) =>
        StateChanged?.Invoke(this, new LocalApiStateChangedEventArgs(topic, BuildSnapshot()));

    private LocalApiSnapshot BuildSnapshot()
    {
        var allWindows = workspaces.GetAllWorkspaceWindows();
        var windows = new List<LocalApiWindow>();
        foreach (var pair in allWindows.OrderBy(pair => pair.Key))
        {
            foreach (var handle in pair.Value.OrderBy(value => value.ToInt64()))
            {
                try
                {
                    var metadata = windowService.GetWindowMetadata(handle);
                    if (!metadata.Exists || string.IsNullOrWhiteSpace(metadata.Title)) continue;
                    windows.Add(new LocalApiWindow(
                        $"0x{handle.ToInt64():X}",
                        pair.Key,
                        metadata.Title,
                        metadata.IsVisible,
                        metadata.IsMinimized,
                        metadata.IsOperable));
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
        if (!messageLoop.TryPost(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        }))
        {
            throw new InvalidOperationException("El message loop no acepta trabajo para la API local.");
        }

        return completion.Task.WaitAsync(TimeSpan.FromMilliseconds(LocalApiDefaults.OperationTimeoutMilliseconds)).GetAwaiter().GetResult();
    }
}
