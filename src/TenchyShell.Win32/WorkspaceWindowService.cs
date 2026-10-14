using System.Diagnostics;
using System.Runtime.InteropServices;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

public sealed class WorkspaceWindowService : IWorkspaceWindowService
{
    private readonly uint currentProcessId = (uint)Environment.ProcessId;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly ILogger? logger;

    public WorkspaceWindowService(LiveBenchmarkRecorder? benchmarkRecorder = null, ILogger? logger = null)
    {
        this.benchmarkRecorder = benchmarkRecorder?.IsEnabled == true ? benchmarkRecorder : null;
        this.logger = logger;
    }

    public WorkspaceWindowCapture CaptureSnapshot(string operation = "workspace")
    {
        var startedAt = Stopwatch.GetTimestamp();
        var windows = new List<TenchyShell.Core.Windows.WorkspaceWindowSnapshot>();
        var discarded = 0;
        var metadataQueries = 0;
        var enumWindowsCalls = 0;
        enumWindowsCalls++;

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            metadataQueries++;
            try
            {
                var snapshot = CreateSnapshot(windowHandle);
                var decision = DecideWindowTracking(snapshot, currentProcessId);
                if (decision.Include)
                {
                    windows.Add(new TenchyShell.Core.Windows.WorkspaceWindowSnapshot(
                        windowHandle,
                        new WorkspaceWindowMetadata(
                            Exists: true,
                            IsVisible: snapshot.IsVisible,
                            IsMinimized: NativeMethods.IsIconic(windowHandle),
                            Title: snapshot.Title,
                            IsOperable: true),
                        snapshot.ProcessId));
                }
                else
                {
                    discarded++;
                }
            }
            catch
            {
                // Una ventana puede desaparecer o denegar metadata durante EnumWindows.
                discarded++;
            }

            return true;
        }, IntPtr.Zero);

        var capture = new WorkspaceWindowCapture(
            operation,
            windows,
            enumWindowsCalls,
            metadataQueries,
            discarded,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        RecordCaptureTelemetry(capture);
        return capture;
    }

    public bool RevalidateWindow(TenchyShell.Core.Windows.WorkspaceWindowSnapshot snapshot)
    {
        if (snapshot.Handle == IntPtr.Zero || !NativeMethods.IsWindow(snapshot.Handle)) return false;
        var processId = 0u;
        if (snapshot.ProcessId != 0 && NativeMethods.GetWindowThreadProcessId(snapshot.Handle, out processId) == 0) return false;
        if (snapshot.ProcessId != 0 && processId != snapshot.ProcessId) return false;
        if (!string.IsNullOrWhiteSpace(snapshot.Title) && NativeMethods.GetWindowTitleOrEmpty(snapshot.Handle) != snapshot.Title) return false;
        return true;
    }

    public IReadOnlyList<IntPtr> GetVisibleTopLevelWindows()
    {
        return CaptureSnapshot("workspace").Windows.Select(window => window.Handle).ToArray();
    }

    // El selector necesita el valor Win32 sin sustitutos: WindowSwitcherState
    // descarta los HWND cuyo título desapareció desde el último Refresh.
    public string GetWindowTitle(IntPtr windowHandle) => NativeMethods.GetWindowTitleOrEmpty(windowHandle);

    public bool WindowExists(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero && NativeMethods.IsWindow(windowHandle);

    public WorkspaceWindowMetadata GetWindowMetadata(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return new WorkspaceWindowMetadata(false, false, false, string.Empty, false);
        }

        var snapshot = CreateSnapshot(windowHandle);
        var decision = DecideWindowTracking(snapshot, currentProcessId);
        return new WorkspaceWindowMetadata(
            Exists: true,
            IsVisible: snapshot.IsVisible,
            IsMinimized: NativeMethods.IsIconic(windowHandle),
            Title: snapshot.Title,
            IsOperable: decision.Include,
            ProcessId: snapshot.ProcessId);
    }

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public void SetVisible(IntPtr windowHandle, bool visible)
    {
        NativeMethods.ShowWindow(windowHandle, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
    }

    public WorkspaceFocusResult RestoreAndFocus(IntPtr windowHandle)
    {
        var metadata = GetWindowMetadata(windowHandle);
        if (!metadata.Exists || !metadata.IsOperable)
        {
            return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        }

        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
        return Focus(windowHandle);
    }

    public WorkspaceFocusResult RestoreAndFocus(TenchyShell.Core.Windows.WorkspaceWindowSnapshot snapshot)
    {
        if (!RevalidateWindow(snapshot)) return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        NativeMethods.ShowWindow(snapshot.Handle, NativeMethods.SW_RESTORE);
        return Focus(snapshot.Handle);
    }

    public WorkspaceFocusResult Focus(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle)) return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        if (NativeMethods.IsIconic(windowHandle)) NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
        var initialForegroundResult = NativeMethods.SetForegroundWindow(windowHandle);
        if (NativeMethods.GetForegroundWindow() == windowHandle)
        {
            RecordFocusResult(windowHandle, "initial", initialForegroundResult, attachedToTarget: null, attachedToForeground: null);
            return WorkspaceFocusResult.Success();
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(windowHandle, out _);
        var foregroundThread = foreground == IntPtr.Zero
            ? 0u
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);

        // El selector es normalmente la ventana activa. Unir únicamente su hilo al
        // hilo activo no concede foco a la ventana destino; hay que unir también
        // (o, en el caso habitual, directamente) el hilo de la ventana elegida.
        var needsTargetAttachment = targetThread != 0 && targetThread != currentThread;
        var attachedToTarget = needsTargetAttachment && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        var targetAttachmentError = 0;
        if (needsTargetAttachment && !attachedToTarget)
        {
            targetAttachmentError = Marshal.GetLastWin32Error();
            benchmarkRecorder?.Record("window_focus_attach_failed", new
            {
                targetHandle = windowHandle.ToInt64(),
                attachment = "target",
                currentThread,
                targetThread,
                errorCode = targetAttachmentError
            });
        }

        var needsForegroundAttachment = foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread;
        var attachedToForeground = needsForegroundAttachment && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        if (needsForegroundAttachment && !attachedToForeground)
        {
            benchmarkRecorder?.Record("window_focus_attach_failed", new
            {
                targetHandle = windowHandle.ToInt64(),
                attachment = "foreground",
                currentThread,
                foregroundThread,
                errorCode = Marshal.GetLastWin32Error()
            });
        }

        var retryForegroundResult = false;
        var focused = false;
        try
        {
            retryForegroundResult = NativeMethods.SetForegroundWindow(windowHandle);
            if (NativeMethods.GetForegroundWindow() == windowHandle)
            {
                focused = true;
                return WorkspaceFocusResult.Success();
            }

            // SetFocus sólo es válido entre hilos con la misma cola de entrada;
            // AttachThreadInput arriba satisface esa condición cuando Windows
            // rechazó el cambio de primer plano inicial.
            if (attachedToTarget) NativeMethods.SetFocus(windowHandle);
            focused = NativeMethods.GetForegroundWindow() == windowHandle;
            return focused
                ? WorkspaceFocusResult.Success()
                : WorkspaceFocusResult.Failed(targetAttachmentError == 5
                    ? WorkspaceFocusFailure.AccessDenied
                    : WorkspaceFocusFailure.WindowsRejected);
        }
        finally
        {
            RecordFocusResult(windowHandle, "retry", retryForegroundResult, attachedToTarget, attachedToForeground, focused);
            if (attachedToForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedToTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private void RecordFocusResult(
        IntPtr windowHandle,
        string stage,
        bool setForegroundResult,
        bool? attachedToTarget,
        bool? attachedToForeground,
        bool? focused = null)
    {
        benchmarkRecorder?.Record("window_focus_result", new
        {
            targetHandle = windowHandle.ToInt64(),
            stage,
            setForegroundResult,
            attachedToTarget,
            attachedToForeground,
            focused = focused ?? NativeMethods.GetForegroundWindow() == windowHandle
        });
    }

    private WorkspaceWindowSnapshot CreateSnapshot(IntPtr windowHandle)
    {
        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        var extendedStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        var rootOwner = NativeMethods.GetAncestor(windowHandle, NativeMethods.GA_ROOTOWNER);
        if (rootOwner == IntPtr.Zero) rootOwner = windowHandle;

        return new WorkspaceWindowSnapshot(
            windowHandle,
            NativeMethods.IsWindowVisible(windowHandle),
            processId,
            NativeMethods.GetWindowTitleOrEmpty(windowHandle),
            NativeMethods.GetWindowClassName(windowHandle),
            NativeMethods.GetWindow(windowHandle, NativeMethods.GW_OWNER),
            rootOwner,
            GetAltTabRepresentative(rootOwner),
            extendedStyle,
            NativeMethods.IsWindowCloaked(windowHandle));
    }

    private void RecordCaptureTelemetry(WorkspaceWindowCapture capture)
    {
        var details = new
        {
            operation = capture.Operation,
            enumWindows = capture.EnumWindowsCalls,
            metadataQueries = capture.MetadataQueries,
            durationMs = capture.DurationMilliseconds,
            discardedHandles = capture.DiscardedHandles,
            revalidationFailures = capture.RevalidationFailures,
            includedCount = capture.Windows.Count
        };
        logger?.Info($"Snapshot de ventanas ({capture.Operation}): EnumWindows={capture.EnumWindowsCalls}, metadata={capture.MetadataQueries}, descartados={capture.DiscardedHandles}, duración={capture.DurationMilliseconds:F2} ms.");
        benchmarkRecorder?.Record("workspace_window_snapshot", details);
    }

    private static IntPtr GetAltTabRepresentative(IntPtr rootOwner)
    {
        var candidate = rootOwner;
        while (candidate != IntPtr.Zero)
        {
            var extendedStyle = NativeMethods.GetWindowLongPtr(candidate, NativeMethods.GWL_EXSTYLE).ToInt64();
            if (NativeMethods.IsWindowVisible(candidate) && (extendedStyle & NativeMethods.WS_EX_TOOLWINDOW) == 0)
            {
                return candidate;
            }

            var popup = NativeMethods.GetLastActivePopup(candidate);
            if (popup == IntPtr.Zero || popup == candidate) break;
            candidate = popup;
        }

        return IntPtr.Zero;
    }

    internal static WorkspaceWindowDecision DecideWindowTracking(WorkspaceWindowSnapshot window, uint currentProcessId)
    {
        if (!window.IsVisible) return new(false, "not_visible");
        if (window.ProcessId == currentProcessId) return new(false, "tenchyshell_window");
        if (window.ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return new(false, "windows_shell");
        if ((window.ExtendedStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return new(false, "tool_window");
        if (string.IsNullOrWhiteSpace(window.Title)) return new(false, "empty_title");
        if (window.IsCloaked) return new(false, "cloaked");
        return window.AltTabRepresentative == window.Handle
            ? new(true, "alt_tab_representative")
            : new(false, "owned_or_popup_window");
    }
}

internal readonly record struct WorkspaceWindowSnapshot(
    IntPtr Handle,
    bool IsVisible,
    uint ProcessId,
    string Title,
    string ClassName,
    IntPtr Owner,
    IntPtr RootOwner,
    IntPtr AltTabRepresentative,
    long ExtendedStyle,
    bool IsCloaked);

internal readonly record struct WorkspaceWindowDecision(bool Include, string Reason);
