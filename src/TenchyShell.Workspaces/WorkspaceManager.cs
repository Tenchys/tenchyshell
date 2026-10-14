using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Windows;

namespace TenchyShell.Workspaces;

public sealed class WorkspaceManager : IWorkspaceWindowSource
{
    public const int FirstWorkspace = 1;
    public const int LastWorkspace = 9;

    private readonly IWorkspaceWindowService windowService;
    private readonly ILogger? logger;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly Dictionary<int, HashSet<IntPtr>> windowsByWorkspace =
        Enumerable.Range(FirstWorkspace, LastWorkspace)
            .ToDictionary(workspace => workspace, _ => new HashSet<IntPtr>());
    private readonly Dictionary<IntPtr, int> workspaceByWindow = new();
    private int currentWorkspace = FirstWorkspace;

    public WorkspaceManager(IWorkspaceWindowService windowService, ILogger? logger = null, LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.windowService = windowService;
        this.logger = logger;
        this.benchmarkRecorder = benchmarkRecorder?.IsEnabled == true ? benchmarkRecorder : null;
    }

    public int CurrentWorkspace => currentWorkspace;

    public event EventHandler? StateChanged;

    public IReadOnlyList<IntPtr> GetCurrentWorkspaceWindows() =>
        windowsByWorkspace[currentWorkspace].ToArray();

    public IReadOnlyDictionary<int, IReadOnlyList<IntPtr>> GetAllWorkspaceWindows() =>
        windowsByWorkspace.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<IntPtr>)pair.Value.ToArray());

    public WorkspaceOperationSnapshot CaptureCurrentWorkspaceSnapshot(string operation = "workspace")
    {
        var capture = windowService.CaptureSnapshot(operation);
        var capturedByHandle = capture.Windows.ToDictionary(window => window.Handle);

        foreach (var windowHandle in workspaceByWindow.Keys.ToArray())
        {
            if (!windowService.WindowExists(windowHandle))
            {
                capture.RecordDiscardedHandle();
                RemoveWindow(windowHandle);
            }
        }

        foreach (var window in capture.Windows)
        {
            if (!workspaceByWindow.ContainsKey(window.Handle))
            {
                windowsByWorkspace[currentWorkspace].Add(window.Handle);
                workspaceByWindow[window.Handle] = currentWorkspace;
            }
        }

        var windowsByWorkspaceSnapshot = new Dictionary<int, IReadOnlyList<WorkspaceWindowSnapshot>>();
        foreach (var pair in windowsByWorkspace)
        {
            var snapshots = new List<WorkspaceWindowSnapshot>();
            foreach (var handle in pair.Value)
            {
                if (capturedByHandle.TryGetValue(handle, out var snapshot))
                {
                    snapshots.Add(snapshot);
                    continue;
                }

                // Las ventanas ocultas por TenchyShell no aparecen en EnumWindows
                // como candidatas visibles; se consultan una vez dentro de esta
                // operación, sin conservar metadata para acciones posteriores.
                var metadata = windowService.GetWindowMetadata(handle);
                capture.RecordMetadataQuery();
                if (metadata.Exists)
                {
                    snapshots.Add(new WorkspaceWindowSnapshot(handle, metadata));
                }
                else
                {
                    capture.RecordDiscardedHandle();
                }
            }

            windowsByWorkspaceSnapshot[pair.Key] = snapshots;
        }

        var operationSnapshot = new WorkspaceOperationSnapshot(
            operation,
            currentWorkspace,
            windowsByWorkspaceSnapshot,
            capture);
        RecordOperationTelemetry(operationSnapshot);
        return operationSnapshot;
    }

    public void Refresh()
    {
        CaptureCurrentWorkspaceSnapshot("workspace_refresh");
    }

    public WorkspaceOperationResult SwitchTo(int workspace)
    {
        if (!IsValidWorkspace(workspace))
        {
            return WorkspaceOperationResult.Failure($"El workspace {workspace} no es válido. Usa un valor entre 1 y 9.");
        }

        var operation = CaptureCurrentWorkspaceSnapshot("workspace_switch");

        if (workspace == currentWorkspace)
        {
            return WorkspaceOperationResult.Success();
        }

        foreach (var snapshot in operation.WindowsByWorkspace.GetValueOrDefault(currentWorkspace, Array.Empty<WorkspaceWindowSnapshot>()))
        {
            if (TryRevalidate(operation, snapshot)) windowService.SetVisible(snapshot.Handle, visible: false);
        }

        currentWorkspace = workspace;
        var targetWindows = operation.WindowsByWorkspace.GetValueOrDefault(currentWorkspace, Array.Empty<WorkspaceWindowSnapshot>());

        foreach (var snapshot in targetWindows)
        {
            if (TryRevalidate(operation, snapshot)) windowService.SetVisible(snapshot.Handle, visible: true);
        }

        var foreground = targetWindows.FirstOrDefault(snapshot => TryRevalidate(operation, snapshot));
        if (foreground.Handle != IntPtr.Zero)
        {
            windowService.Focus(foreground.Handle);
        }

        RecordOperationTelemetry(operation);

        StateChanged?.Invoke(this, EventArgs.Empty);

        return WorkspaceOperationResult.Success();
    }

    public WorkspaceOperationResult MoveForegroundTo(int workspace)
    {
        if (!IsValidWorkspace(workspace))
        {
            return WorkspaceOperationResult.Failure($"El workspace {workspace} no es válido. Usa un valor entre 1 y 9.");
        }

        var foregroundWindow = windowService.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return WorkspaceOperationResult.Failure("No hay una ventana activa para mover.");
        }

        var operation = CaptureCurrentWorkspaceSnapshot("workspace_move");
        if (workspaceByWindow.TryGetValue(foregroundWindow, out var previousWorkspace))
        {
            windowsByWorkspace[previousWorkspace].Remove(foregroundWindow);
        }

        windowsByWorkspace[workspace].Add(foregroundWindow);
        workspaceByWindow[foregroundWindow] = workspace;

        if (workspace == currentWorkspace)
        {
            var snapshot = FindSnapshot(operation, foregroundWindow);
            if (snapshot is null || !TryRevalidate(operation, snapshot.Value))
            {
                return WorkspaceOperationResult.Failure("La ventana activa dejó de ser válida antes de moverla.");
            }

            windowService.SetVisible(foregroundWindow, visible: true);
            if (!TryRevalidate(operation, snapshot.Value))
            {
                return WorkspaceOperationResult.Failure("La ventana activa dejó de ser válida antes de enfocarla.");
            }
            windowService.Focus(foregroundWindow);
        }
        else
        {
            var snapshot = FindSnapshot(operation, foregroundWindow);
            if (snapshot is not null && TryRevalidate(operation, snapshot.Value))
            {
                windowService.SetVisible(foregroundWindow, visible: false);
            }
        }

        RecordOperationTelemetry(operation);

        StateChanged?.Invoke(this, EventArgs.Empty);

        return WorkspaceOperationResult.Success();
    }

    private void RemoveWindow(IntPtr windowHandle)
    {
        if (!workspaceByWindow.Remove(windowHandle, out var workspace))
        {
            return;
        }

        windowsByWorkspace[workspace].Remove(windowHandle);
    }

    private static bool IsValidWorkspace(int workspace) => workspace is >= FirstWorkspace and <= LastWorkspace;

    private WorkspaceWindowSnapshot? FindSnapshot(WorkspaceOperationSnapshot operation, IntPtr handle) =>
        operation.WindowsByWorkspace.Values.SelectMany(windows => windows).FirstOrDefault(snapshot => snapshot.Handle == handle);

    private bool TryRevalidate(WorkspaceOperationSnapshot operation, WorkspaceWindowSnapshot snapshot)
    {
        var valid = windowService.RevalidateWindow(snapshot);
        if (!valid) operation.Capture?.RecordRevalidationFailure();
        return valid;
    }

    private void RecordOperationTelemetry(WorkspaceOperationSnapshot operation)
    {
        var capture = operation.Capture;
        if (capture is null) return;
        logger?.Info($"Operación de ventanas ({operation.Operation}): EnumWindows={capture.EnumWindowsCalls}, metadata={capture.MetadataQueries}, descartados={capture.DiscardedHandles}, revalidaciones fallidas={capture.RevalidationFailures}, duración={capture.DurationMilliseconds:F2} ms.");
        benchmarkRecorder?.Record("workspace_window_operation", new
        {
            operation = operation.Operation,
            enumWindows = capture.EnumWindowsCalls,
            metadataQueries = capture.MetadataQueries,
            durationMs = capture.DurationMilliseconds,
            discardedHandles = capture.DiscardedHandles,
            revalidationFailures = capture.RevalidationFailures
        });
    }
}
