using System.Diagnostics;

namespace TenchyShell.Win32;

public sealed record ExplorerShellExitResult(bool Succeeded, string Message, int? ProcessId = null);

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
        if (initialProcesses.Count != 1)
        {
            return new ExplorerShellExitResult(
                false,
                $"Se requiere exactamente un explorer.exe en la sesión {sessionId}; se encontraron {initialProcesses.Count}.");
        }

        var initialProcessId = initialProcesses[0];
        var trayWindow = platform.FindShellTrayWindow();
        if (trayWindow == IntPtr.Zero)
        {
            return new ExplorerShellExitResult(
                false,
                "No se encontró la ventana Shell_TrayWnd de Explorer.",
                initialProcessId);
        }

        var trayProcessId = platform.GetWindowProcessId(trayWindow);
        if (trayProcessId != initialProcessId)
        {
            return new ExplorerShellExitResult(
                false,
                $"Shell_TrayWnd pertenece al PID {trayProcessId}, no al explorer.exe esperado (PID {initialProcessId}).",
                initialProcessId);
        }

        if (!platform.PostExplorerExit(trayWindow))
        {
            return new ExplorerShellExitResult(
                false,
                $"Explorer rechazó la solicitud cooperativa de salida (error Win32 {platform.LastError}).",
                initialProcessId);
        }

        var requiredStableSamples = Math.Max(1, (int)Math.Ceiling(stablePeriod / pollInterval));
        var maximumSamples = Math.Max(requiredStableSamples, (int)Math.Ceiling(timeout / pollInterval));
        var absentSamples = 0;

        for (var sample = 0; sample < maximumSamples; sample++)
        {
            platform.Delay(pollInterval);
            var currentProcesses = platform.GetExplorerProcessIds(sessionId);
            if (currentProcesses.Count == 0)
            {
                absentSamples++;
                if (absentSamples >= requiredStableSamples)
                {
                    return new ExplorerShellExitResult(
                        true,
                        $"Explorer terminó de forma cooperativa y permaneció ausente durante {stablePeriod.TotalSeconds:0.#} s.",
                        initialProcessId);
                }

                continue;
            }

            absentSamples = 0;
            if (currentProcesses.Count != 1)
            {
                return new ExplorerShellExitResult(
                    false,
                    $"El estado de Explorer se volvió ambiguo: se encontraron {currentProcesses.Count} procesos.",
                    initialProcessId);
            }

            if (currentProcesses[0] != initialProcessId)
            {
                return new ExplorerShellExitResult(
                    false,
                    $"Windows relanzó explorer.exe con PID {currentProcesses[0]}; se canceló el modo sin Explorer.",
                    initialProcessId);
            }
        }

        return new ExplorerShellExitResult(
            false,
            $"explorer.exe (PID {initialProcessId}) no terminó de forma estable dentro de {timeout.TotalSeconds:0.#} s.",
            initialProcessId);
    }
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
