namespace TenchyShell.Core.Windows;

public interface IWorkspaceWindowService
{
    /// <summary>Captura el estado de las ventanas una sola vez para una operación.</summary>
    WorkspaceWindowCapture CaptureSnapshot(string operation = "workspace")
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var windows = new List<WorkspaceWindowSnapshot>();
        foreach (var handle in GetVisibleTopLevelWindows())
        {
            var metadata = GetWindowMetadata(handle);
            windows.Add(new WorkspaceWindowSnapshot(handle, metadata));
        }

        return new WorkspaceWindowCapture(
            operation,
            windows,
            enumWindowsCalls: 1,
            metadataQueries: windows.Count,
            discardedHandles: 0,
            durationMilliseconds: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Revalida identidad y existencia justo antes de modificar un HWND.</summary>
    bool RevalidateWindow(WorkspaceWindowSnapshot snapshot) =>
        snapshot.Handle != IntPtr.Zero && WindowExists(snapshot.Handle);

    IReadOnlyList<IntPtr> GetVisibleTopLevelWindows();

    string GetWindowTitle(IntPtr windowHandle);

    /// <summary>Indica si el HWND todavía existe sin consultar metadata adicional.</summary>
    bool WindowExists(IntPtr windowHandle) => windowHandle != IntPtr.Zero;

    IntPtr GetForegroundWindow();

    void SetVisible(IntPtr windowHandle, bool visible);

    WorkspaceFocusResult Focus(IntPtr windowHandle);

    /// <summary>Obtiene metadatos sin modificar el estado de la ventana.</summary>
    WorkspaceWindowMetadata GetWindowMetadata(IntPtr windowHandle) =>
        new(
            windowHandle != IntPtr.Zero,
            windowHandle != IntPtr.Zero,
            false,
            GetWindowTitle(windowHandle),
            true);

    /// <summary>Restaura una ventana minimizada y solicita su foco.</summary>
    WorkspaceFocusResult RestoreAndFocus(IntPtr windowHandle) => Focus(windowHandle);

    /// <summary>Restaura usando la identidad capturada en la operación actual.</summary>
    WorkspaceFocusResult RestoreAndFocus(WorkspaceWindowSnapshot snapshot)
    {
        if (!RevalidateWindow(snapshot)) return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        return RestoreAndFocus(snapshot.Handle);
    }
}

public readonly record struct WorkspaceWindowMetadata(
    bool Exists,
    bool IsVisible,
    bool IsMinimized,
    string Title,
    bool IsOperable,
    uint ProcessId = 0);

public readonly record struct WorkspaceWindowSnapshot(IntPtr Handle, WorkspaceWindowMetadata Metadata, uint ProcessId = 0)
{
    public WorkspaceWindowSnapshot(IntPtr handle, WorkspaceWindowMetadata metadata)
        : this(handle, metadata, metadata.ProcessId) { }

    public bool Exists => Metadata.Exists;
    public bool IsVisible => Metadata.IsVisible;
    public bool IsMinimized => Metadata.IsMinimized;
    public string Title => Metadata.Title;
    public bool IsOperable => Metadata.IsOperable;
}

/// <summary>Datos efímeros de una enumeración; nunca se conserva entre acciones.</summary>
public sealed class WorkspaceWindowCapture
{
    public WorkspaceWindowCapture(
        string operation,
        IReadOnlyList<WorkspaceWindowSnapshot> windows,
        int enumWindowsCalls,
        int metadataQueries,
        int discardedHandles,
        double durationMilliseconds)
    {
        Operation = operation;
        Windows = windows;
        EnumWindowsCalls = enumWindowsCalls;
        MetadataQueries = metadataQueries;
        DiscardedHandles = discardedHandles;
        DurationMilliseconds = durationMilliseconds;
    }

    public string Operation { get; }
    public IReadOnlyList<WorkspaceWindowSnapshot> Windows { get; }
    public int EnumWindowsCalls { get; }
    public int MetadataQueries { get; private set; }
    public int DiscardedHandles { get; private set; }
    public int RevalidationFailures { get; private set; }
    public double DurationMilliseconds { get; }

    public void RecordDiscardedHandle() => DiscardedHandles++;
    public void RecordMetadataQuery() => MetadataQueries++;
    public void RecordRevalidationFailure() => RevalidationFailures++;
}

public readonly record struct WorkspaceFocusResult(bool Succeeded, WorkspaceFocusFailure Failure)
{
    public static WorkspaceFocusResult Success() => new(true, WorkspaceFocusFailure.None);

    public static WorkspaceFocusResult Failed(WorkspaceFocusFailure failure) => new(false, failure);
}

public enum WorkspaceFocusFailure
{
    None,
    InvalidWindow,
    AccessDenied,
    WindowsRejected
}
