using System.Diagnostics;

namespace TenchyShell.Win32;

public enum ExplorerShellState
{
    Stopped,
    Running,
    ResidualProcess,
    Ambiguous
}

public sealed record ExplorerShellExitResult(bool Succeeded, ExplorerShellState State, string Message, int? ProcessId = null)
{
    /// <summary>Indica si la sesion cumple el contrato de <c>--without-explorer</c>.</summary>
    public bool CanContinueWithoutExplorer => Succeeded && State == ExplorerShellState.Stopped;
}

/// <summary>
/// Solicita la salida cooperativa del shell de Explorer en la sesion actual.
/// Este servicio solo se usa en el modo explicito <c>--without-explorer</c>.
/// </summary>
public sealed class ExplorerShellController
{
    private const int MaximumAttempts = 4;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultStablePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private readonly IExplorerShellPlatform platform;

    public ExplorerShellController()
        : this(new ExplorerShellPlatform())
    {
    }

    internal ExplorerShellController(IExplorerShellPlatform platform)
    {
        this.platform = platform;
    }

    public ExplorerShellExitResult TryExitCurrentSession()
    {
        return TryExitCurrentSession(DefaultTimeout, DefaultStablePeriod, DefaultPollInterval);
    }

    internal ExplorerShellExitResult TryExitCurrentSession(
        TimeSpan timeout,
        TimeSpan stablePeriod,
        TimeSpan pollInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stablePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);

        var sessionId = platform.CurrentSessionId;
        var requiredStableSamples = Math.Max(1, (int)Math.Ceiling(stablePeriod / pollInterval));
        var maximumSamples = Math.Max(requiredStableSamples, (int)Math.Ceiling(timeout / pollInterval));
        var attemptMessages = new List<string>(MaximumAttempts);
        ExplorerShellExitResult? lastFailure = null;

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            lastFailure = null;
            var processes = platform.GetExplorerProcessIds(sessionId);
            var trayWindow = platform.FindShellTrayWindow();

            // An already clean session is the fast path. Once a shutdown is
            // requested, both the tray and every explorer.exe must remain gone.
            if (attempt == 1 && trayWindow == IntPtr.Zero && processes.Count == 0)
            {
                return DescribeAbsentShell(processes, "Shell_TrayWnd ya estaba ausente; no se solicito ninguna salida.");
            }

            if (processes.Count > 1 || (trayWindow != IntPtr.Zero && processes.Count != 1))
            {
                return new ExplorerShellExitResult(
                    false,
                    ExplorerShellState.Ambiguous,
                    $"Intento {attempt}/{MaximumAttempts}: se requiere exactamente un explorer.exe asociado a Shell_TrayWnd; se encontraron {processes.Count} procesos.",
                    processes.Count == 1 ? processes[0] : null);
            }

            var processId = processes.Count == 1 ? processes[0] : (int?)null;
            if (trayWindow != IntPtr.Zero)
            {
                var trayProcessId = platform.GetWindowProcessId(trayWindow);
                if (trayProcessId != processId)
                {
                    return new ExplorerShellExitResult(
                        false,
                        ExplorerShellState.Ambiguous,
                        $"Intento {attempt}/{MaximumAttempts}: Shell_TrayWnd pertenece al PID {trayProcessId}, no al explorer.exe esperado (PID {processId}).",
                        processId);
                }

                if (!platform.PostExplorerExit(trayWindow))
                {
                    lastFailure = new ExplorerShellExitResult(
                        false,
                        ExplorerShellState.Running,
                        $"Intento {attempt}/{MaximumAttempts}: Explorer rechazó la solicitud cooperativa de salida (error Win32 {platform.LastError}).",
                        processId);
                    attemptMessages.Add(lastFailure.Message);
                    if (attempt < MaximumAttempts)
                    {
                        platform.Delay(RetryDelay);
                        continue;
                    }

                    break;
                }
            }

            var stableSamples = 0;
            var trayWasAbsent = trayWindow == IntPtr.Zero;
            var lastTrayWindow = trayWindow;
            var lastProcesses = processes;

            for (var sample = 0; sample < maximumSamples; sample++)
            {
                platform.Delay(pollInterval);
                lastTrayWindow = platform.FindShellTrayWindow();
                lastProcesses = platform.GetExplorerProcessIds(sessionId);

                if (lastProcesses.Count > 1)
                {
                    return new ExplorerShellExitResult(
                        false,
                        ExplorerShellState.Ambiguous,
                        $"Intento {attempt}/{MaximumAttempts}: el estado de Explorer se volvió ambiguo; se encontraron {lastProcesses.Count} procesos.",
                        processId);
                }

                if (lastTrayWindow == IntPtr.Zero)
                {
                    trayWasAbsent = true;
                    if (lastProcesses.Count == 0)
                    {
                        stableSamples++;
                        if (stableSamples >= requiredStableSamples)
                        {
                            var successPrefix = attemptMessages.Count == 0
                                ? $"Intento {attempt}/{MaximumAttempts}:"
                                : $"Intentos previos: {string.Join(" | ", attemptMessages)}. Intento {attempt}/{MaximumAttempts}:";
                            return DescribeAbsentShell(
                                lastProcesses,
                                $"{successPrefix} Shell_TrayWnd y todos los procesos explorer.exe permanecieron ausentes durante {stablePeriod.TotalSeconds:0.#} s.");
                        }
                    }
                    else
                    {
                        // A missing tray alone is not enough: wait for the
                        // residual process to exit and restart the stability window.
                        stableSamples = 0;
                    }

                    continue;
                }

                stableSamples = 0;
                if (lastProcesses.Count != 1)
                {
                    return new ExplorerShellExitResult(
                        false,
                        ExplorerShellState.Ambiguous,
                        $"Intento {attempt}/{MaximumAttempts}: Shell_TrayWnd reapareció sin un único proceso explorer.exe asociado.",
                        processId);
                }

                var currentProcessId = lastProcesses[0];
                if (processId.HasValue && currentProcessId != processId.Value)
                {
                    return new ExplorerShellExitResult(
                        false,
                        ExplorerShellState.Running,
                        $"Intento {attempt}/{MaximumAttempts}: Windows relanzó explorer.exe con PID {currentProcessId}; se canceló el intento actual.",
                        currentProcessId);
                }

                var currentTrayProcessId = platform.GetWindowProcessId(lastTrayWindow);
                if (currentTrayProcessId != currentProcessId)
                {
                    return new ExplorerShellExitResult(
                        false,
                        ExplorerShellState.Ambiguous,
                        $"Intento {attempt}/{MaximumAttempts}: Shell_TrayWnd pertenece al PID {currentTrayProcessId}, no al proceso actual (PID {currentProcessId}).",
                        currentProcessId);
                }

                if (trayWasAbsent)
                {
                    // The tray returned during this attempt. Ask that shell to
                    // exit again instead of treating the missing tray as enough.
                    trayWasAbsent = false;
                    if (!platform.PostExplorerExit(lastTrayWindow))
                    {
                        lastFailure = new ExplorerShellExitResult(
                            false,
                            ExplorerShellState.Running,
                        $"Intento {attempt}/{MaximumAttempts}: Shell_TrayWnd reapareció y Explorer rechazó la nueva solicitud cooperativa (error Win32 {platform.LastError}).",
                            currentProcessId);
                        break;
                    }
                }
            }

            var failureState = lastTrayWindow == IntPtr.Zero && lastProcesses.Count == 1
                ? ExplorerShellState.ResidualProcess
                : ExplorerShellState.Running;
            var residualPid = lastProcesses.Count == 1 ? lastProcesses[0] : processId;
            if (lastFailure is null)
            {
                var reason = failureState == ExplorerShellState.ResidualProcess
                    ? $"Shell_TrayWnd ausente pero explorer.exe (PID {residualPid}) permanece como proceso residual."
                    : $"Shell_TrayWnd de explorer.exe (PID {residualPid}) no desaparecio junto con el proceso de forma estable dentro de {timeout.TotalSeconds:0.#} s.";
                lastFailure = new ExplorerShellExitResult(false, failureState, $"Intento {attempt}/{MaximumAttempts}: {reason}", residualPid);
            }

            if (!attemptMessages.Contains(lastFailure.Message, StringComparer.Ordinal))
            {
                attemptMessages.Add(lastFailure.Message);
            }

            if (attempt < MaximumAttempts)
            {
                platform.Delay(RetryDelay);
            }
        }

        var final = lastFailure ?? new ExplorerShellExitResult(false, ExplorerShellState.Running, "No se pudo determinar el estado de Explorer.");
        return final with
        {
            Message = $"Se agotaron {MaximumAttempts} intentos para cerrar Explorer cooperativamente. {string.Join(" | ", attemptMessages)}",
            Succeeded = false
        };
    }

    public ExplorerShellExitResult GetCurrentSessionState()
    {
        var processes = platform.GetExplorerProcessIds(platform.CurrentSessionId);
        var tray = platform.FindShellTrayWindow();
        if (tray == IntPtr.Zero) return DescribeAbsentShell(processes, "Estado actual de Explorer.");
        if (processes.Count == 1 && platform.GetWindowProcessId(tray) == processes[0])
        {
            return new ExplorerShellExitResult(true, ExplorerShellState.Running, "Explorer actua como shell en la sesion actual.", processes[0]);
        }
        return new ExplorerShellExitResult(false, ExplorerShellState.Ambiguous, "Explorer tiene una bandeja o procesos ambiguos en la sesion actual.");
    }

    private static ExplorerShellExitResult DescribeAbsentShell(IReadOnlyList<int> processes, string prefix) => processes.Count switch
    {
        0 => new ExplorerShellExitResult(true, ExplorerShellState.Stopped, $"{prefix} Explorer no tiene procesos en la sesion actual."),
        1 => new ExplorerShellExitResult(false, ExplorerShellState.ResidualProcess, $"{prefix} explorer.exe (PID {processes[0]}) permanece como proceso residual sin bandeja.", processes[0]),
        _ => new ExplorerShellExitResult(false, ExplorerShellState.Ambiguous, $"{prefix} Se encontraron {processes.Count} procesos explorer.exe sin bandeja.")
    };
}

internal interface IExplorerShellPlatform
{
    int CurrentSessionId { get; }
    int LastError { get; }
    IReadOnlyList<int> GetExplorerProcessIds(int sessionId);
    IntPtr FindShellTrayWindow();
    int GetWindowProcessId(IntPtr window);
    bool PostExplorerExit(IntPtr window);
    void Delay(TimeSpan duration);
}

internal sealed class ExplorerShellPlatform : IExplorerShellPlatform
{
    // Mensaje usado por la accion interactiva "Salir de Explorer" de la barra.
    // No es un contrato publico de Win32: se valida el HWND/PID y se falla de
    // forma segura si una version futura de Windows deja de admitirlo.
    private const uint ExplorerExitMessage = NativeMethods.WM_USER + 436;

    public int CurrentSessionId
    {
        get
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
    }

    public int LastError => System.Runtime.InteropServices.Marshal.GetLastWin32Error();

    public IReadOnlyList<int> GetExplorerProcessIds(int sessionId)
    {
        var matching = new List<int>();
        foreach (var explorer in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (explorer.SessionId == sessionId) matching.Add(explorer.Id);
            }
            catch (InvalidOperationException)
            {
                // El proceso termino mientras se consultaba; la siguiente
                // muestra resolvera el estado estable.
            }
            finally
            {
                explorer.Dispose();
            }
        }

        matching.Sort();
        return matching;
    }

    public IntPtr FindShellTrayWindow()
    {
        return NativeMethods.FindWindow("Shell_TrayWnd", null);
    }

    public int GetWindowProcessId(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return checked((int)processId);
    }

    public bool PostExplorerExit(IntPtr window)
    {
        return NativeMethods.PostMessage(window, ExplorerExitMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Delay(TimeSpan duration)
    {
        Thread.Sleep(duration);
    }
}
