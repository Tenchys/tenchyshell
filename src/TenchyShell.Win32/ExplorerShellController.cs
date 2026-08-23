using System.Diagnostics;

namespace TenchyShell.Win32;

public enum ExplorerShellState
{
    Stopped,
    Running,
    ResidualProcess,
    Ambiguous
}

public sealed record ExplorerShellExitResult(bool Succeeded, ExplorerShellState State, string Message, int? ProcessId = null);

/// <summary>
/// Solicita la salida cooperativa del shell de Explorer en la sesión actual.
/// Este servicio solo se usa en el modo explícito <c>--without-explorer</c>.
/// </summary>
public sealed class ExplorerShellController
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultStablePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);
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
        var initialProcesses = platform.GetExplorerProcessIds(sessionId);
        var trayWindow = platform.FindShellTrayWindow();
        if (trayWindow == IntPtr.Zero)
        {
            return DescribeAbsentShell(initialProcesses, "Shell_TrayWnd ya estaba ausente; no se solicitó ninguna salida.");
        }

        if (initialProcesses.Count != 1)
        {
            return new ExplorerShellExitResult(
                false,
                ExplorerShellState.Ambiguous,
                $"Se requiere exactamente un explorer.exe en la sesión {sessionId}; se encontraron {initialProcesses.Count}.");
        }

        var initialProcessId = initialProcesses[0];
        var trayProcessId = platform.GetWindowProcessId(trayWindow);
        if (trayProcessId != initialProcessId)
        {
            return new ExplorerShellExitResult(
                false,
                ExplorerShellState.Ambiguous,
                $"Shell_TrayWnd pertenece al PID {trayProcessId}, no al explorer.exe esperado (PID {initialProcessId}).",
                initialProcessId);
        }

        if (!platform.PostExplorerExit(trayWindow))
        {
            return new ExplorerShellExitResult(
                false,
                ExplorerShellState.Running,
                $"Explorer rechazó la solicitud cooperativa de salida (error Win32 {platform.LastError}).",
                initialProcessId);
        }

        var requiredStableSamples = Math.Max(1, (int)Math.Ceiling(stablePeriod / pollInterval));
        var maximumSamples = Math.Max(requiredStableSamples, (int)Math.Ceiling(timeout / pollInterval));
        var absentTraySamples = 0;

        for (var sample = 0; sample < maximumSamples; sample++)
        {
            platform.Delay(pollInterval);
            var currentTrayWindow = platform.FindShellTrayWindow();
            if (currentTrayWindow == IntPtr.Zero)
            {
                absentTraySamples++;
                if (absentTraySamples >= requiredStableSamples)
                {
                    return DescribeAbsentShell(
                        platform.GetExplorerProcessIds(sessionId),
                        $"Shell_TrayWnd permaneció ausente durante {stablePeriod.TotalSeconds:0.#} s.");
                }

                continue;
            }

            absentTraySamples = 0;
            var currentProcesses = platform.GetExplorerProcessIds(sessionId);
            if (currentProcesses.Count != 1)
            {
                return new ExplorerShellExitResult(
                    false,
                    ExplorerShellState.Ambiguous,
                    $"El estado de Explorer se volvió ambiguo: se encontraron {currentProcesses.Count} procesos.",
                    initialProcessId);
            }

            if (currentProcesses[0] != initialProcessId)
            {
                return new ExplorerShellExitResult(
                    false,
                    ExplorerShellState.Running,
                    $"Windows relanzó explorer.exe con PID {currentProcesses[0]}; se canceló el modo sin Explorer.",
                    initialProcessId);
            }

            var currentTrayProcessId = platform.GetWindowProcessId(currentTrayWindow);
            if (currentTrayProcessId != initialProcessId)
            {
                return new ExplorerShellExitResult(
                    false,
                    ExplorerShellState.Ambiguous,
                    $"Shell_TrayWnd cambió al PID {currentTrayProcessId}; se canceló el modo sin Explorer.",
                    initialProcessId);
            }
        }

        return new ExplorerShellExitResult(
            false,
            ExplorerShellState.Running,
            $"Shell_TrayWnd de explorer.exe (PID {initialProcessId}) no desapareció de forma estable dentro de {timeout.TotalSeconds:0.#} s.",
            initialProcessId);
    }

    public ExplorerShellExitResult GetCurrentSessionState()
    {
        var processes = platform.GetExplorerProcessIds(platform.CurrentSessionId);
        var tray = platform.FindShellTrayWindow();
        if (tray == IntPtr.Zero) return DescribeAbsentShell(processes, "Estado actual de Explorer.");
        if (processes.Count == 1 && platform.GetWindowProcessId(tray) == processes[0])
        {
            return new ExplorerShellExitResult(true, ExplorerShellState.Running, "Explorer actúa como shell en la sesión actual.", processes[0]);
        }
        return new ExplorerShellExitResult(false, ExplorerShellState.Ambiguous, "Explorer tiene una bandeja o procesos ambiguos en la sesión actual.");
    }

    private static ExplorerShellExitResult DescribeAbsentShell(IReadOnlyList<int> processes, string prefix) => processes.Count switch
    {
        0 => new ExplorerShellExitResult(true, ExplorerShellState.Stopped, $"{prefix} Explorer no tiene procesos en la sesión actual."),
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
    // Mensaje usado por la acción interactiva "Salir de Explorer" de la barra.
    // No es un contrato público de Win32: se valida el HWND/PID y se falla de
    // forma segura si una versión futura de Windows deja de admitirlo.
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
                // El proceso terminó mientras se consultaba; la siguiente
                // muestra resolverá el estado estable.
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
