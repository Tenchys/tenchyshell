using System.Diagnostics;
using System.Runtime.InteropServices;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

public sealed class WorkspaceWindowService : IWorkspaceWindowService
{
    private readonly uint currentProcessId = (uint)Environment.ProcessId;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly Dictionary<IntPtr, string> benchmarkSignatures = new();

    public WorkspaceWindowService(LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.benchmarkRecorder = benchmarkRecorder?.IsEnabled == true ? benchmarkRecorder : null;
    }

    public IReadOnlyList<IntPtr> GetVisibleTopLevelWindows()
    {
        var windows = new List<IntPtr>();
        var stopwatch = benchmarkRecorder is null ? null : Stopwatch.StartNew();

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            var snapshot = CreateSnapshot(windowHandle);
            var decision = DecideWindowTracking(snapshot, currentProcessId);
            RecordBenchmarkDecision(snapshot, decision);
            if (decision.Include) windows.Add(windowHandle);
            return true;
        }, IntPtr.Zero);

        if (stopwatch is not null)
        {
            stopwatch.Stop();
            benchmarkRecorder!.Record("workspace_window_enumeration", new
            {
                durationMs = stopwatch.Elapsed.TotalMilliseconds,
                includedCount = windows.Count
            });
        }

        return windows;
    }

    public string GetWindowTitle(IntPtr windowHandle) => NativeMethods.GetWindowTitle(windowHandle);

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public void SetVisible(IntPtr windowHandle, bool visible)
    {
        NativeMethods.ShowWindow(windowHandle, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
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
            NativeMethods.GetWindowTitle(windowHandle),
            NativeMethods.GetWindowClassName(windowHandle),
            NativeMethods.GetWindow(windowHandle, NativeMethods.GW_OWNER),
            rootOwner,
            GetAltTabRepresentative(rootOwner),
            extendedStyle,
            NativeMethods.IsWindowCloaked(windowHandle));
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

    private void RecordBenchmarkDecision(WorkspaceWindowSnapshot snapshot, WorkspaceWindowDecision decision)
    {
        if (benchmarkRecorder is null) return;

        var signature = $"{decision.Include}|{decision.Reason}|{snapshot.ProcessId}|{snapshot.Owner}|{snapshot.RootOwner}|{snapshot.AltTabRepresentative}|{snapshot.ExtendedStyle}|{snapshot.IsVisible}|{snapshot.IsCloaked}|{snapshot.Title}|{snapshot.ClassName}";
        if (benchmarkSignatures.TryGetValue(snapshot.Handle, out var previous) && previous == signature) return;

        benchmarkSignatures[snapshot.Handle] = signature;
        benchmarkRecorder.Record("workspace_window_evaluated", new
        {
            handle = snapshot.Handle.ToInt64(),
            included = decision.Include,
            reason = decision.Reason,
            title = snapshot.Title,
            className = snapshot.ClassName,
            processId = snapshot.ProcessId,
            owner = snapshot.Owner.ToInt64(),
            rootOwner = snapshot.RootOwner.ToInt64(),
            altTabRepresentative = snapshot.AltTabRepresentative.ToInt64(),
            extendedStyle = $"0x{snapshot.ExtendedStyle:X}",
            isVisible = snapshot.IsVisible,
            isCloaked = snapshot.IsCloaked
        });
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
