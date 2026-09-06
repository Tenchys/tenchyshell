using System.Threading;
using TenchyShell.Core.Processes;
using TenchyShell.Core.Windows;

namespace TenchyShell.Core.Diagnostics;

/// <summary>Contadores de sesión sin títulos, HWND ni argumentos de usuario.</summary>
public sealed class SessionStabilityMetrics
{
    private int focusSucceeded;
    private int focusAccessDenied;
    private int focusInvalid;
    private int focusRejected;
    private int packagedSucceeded;
    private int packagedFailed;
    private int explorerTransitions;

    public void RecordFocus(WorkspaceFocusResult result)
    {
        if (result.Succeeded) Interlocked.Increment(ref focusSucceeded);
        else if (result.Failure == WorkspaceFocusFailure.AccessDenied) Interlocked.Increment(ref focusAccessDenied);
        else if (result.Failure == WorkspaceFocusFailure.InvalidWindow) Interlocked.Increment(ref focusInvalid);
        else Interlocked.Increment(ref focusRejected);
    }

    public void RecordPackagedActivation(ProcessLaunchResult result)
    {
        if (result.Succeeded) Interlocked.Increment(ref packagedSucceeded);
        else Interlocked.Increment(ref packagedFailed);
    }

    public void RecordExplorerTransition() => Interlocked.Increment(ref explorerTransitions);

    public SessionStabilitySnapshot Snapshot() => new(
        Volatile.Read(ref focusSucceeded),
        Volatile.Read(ref focusAccessDenied),
        Volatile.Read(ref focusInvalid),
        Volatile.Read(ref focusRejected),
        Volatile.Read(ref packagedSucceeded),
        Volatile.Read(ref packagedFailed),
        Volatile.Read(ref explorerTransitions));
}

public readonly record struct SessionStabilitySnapshot(
    int FocusSucceeded,
    int FocusAccessDenied,
    int FocusInvalid,
    int FocusRejected,
    int PackagedSucceeded,
    int PackagedFailed,
    int ExplorerTransitions)
{
    public override string ToString() =>
        $"foco OK={FocusSucceeded}, foco restringido={FocusAccessDenied}, foco inválido={FocusInvalid}, " +
        $"foco rechazado={FocusRejected}, apps empaquetadas OK={PackagedSucceeded}, " +
        $"apps empaquetadas con error={PackagedFailed}, transiciones Explorer={ExplorerTransitions}";
}
