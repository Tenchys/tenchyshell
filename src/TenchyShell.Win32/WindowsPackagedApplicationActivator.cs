using System.ComponentModel;
using System.Runtime.InteropServices;
using TenchyShell.Core.Applications;
using TenchyShell.Core.Processes;

namespace TenchyShell.Win32;

/// <summary>
/// Activa aplicaciones MSIX/UWP mediante IApplicationActivationManager, sin
/// ShellExecute ni explorer.exe. El launcher normal conserva CLSCTX_INPROC_SERVER.
/// </summary>
public sealed class WindowsPackagedApplicationActivator : IPackagedApplicationActivator, IPackagedApplicationDiagnosticRunner
{
    private readonly IApplicationActivationPlatform platform;

    public WindowsPackagedApplicationActivator()
        : this(new ApplicationActivationPlatform())
    {
    }

    internal WindowsPackagedApplicationActivator(IApplicationActivationPlatform platform)
    {
        this.platform = platform;
    }

    public PackagedApplicationActivationResult Activate(ApplicationEntry application)
    {
        if (string.IsNullOrWhiteSpace(application.AppUserModelId))
        {
            return PackagedApplicationActivationResult.Failure(
                "La aplicación empaquetada no tiene AppUserModelId.",
                unchecked((int)0x80070057),
                PackagedActivationDiagnosticStage.InputValidation,
                "El AppUserModelId está vacío.");
        }

        var attempt = platform.ActivateApplicationOnDedicatedMta(
            application.AppUserModelId,
            string.Join(" ", application.Arguments.Select(QuoteArgument)),
            PackagedApplicationActivationContext.InProcess);
        return attempt.Succeeded
            ? PackagedApplicationActivationResult.Success(
                attempt.ProcessId == 0 ? null : checked((int)attempt.ProcessId),
                attempt.HResult,
                attempt.Stage,
                attempt.Message)
            : PackagedApplicationActivationResult.Failure(
                attempt.TimedOut
                    ? "La activación empaquetada no respondió dentro de cinco segundos."
                    : $"La activación empaquetada devolvió {ToHResultHex(attempt.HResult)}.",
                attempt.HResult,
                attempt.Stage,
                attempt.Message,
                attempt.TimedOut);
    }

    public PackagedActivationDiagnosticResult Diagnose(
        string appUserModelId,
        PackagedApplicationActivationContext context,
        string explorerShellState)
    {
        var startedAt = DateTimeOffset.Now;
        var attempt = string.IsNullOrWhiteSpace(appUserModelId)
            ? PackagedActivationAttempt.Failure(
                unchecked((int)0x80070057),
                "El AppUserModelId está vacío.",
                PackagedActivationDiagnosticStage.InputValidation)
            : platform.ActivateApplicationOnDedicatedMta(appUserModelId, string.Empty, context);
        var finishedAt = DateTimeOffset.Now;
        return new PackagedActivationDiagnosticResult(
            appUserModelId,
            context,
            explorerShellState,
            attempt.Succeeded,
            attempt.ProcessId == 0 ? null : checked((int)attempt.ProcessId),
            attempt.HResult,
            attempt.Stage,
            attempt.Message,
            startedAt,
            finishedAt);
    }

    private static string QuoteArgument(string argument) =>
        argument.Contains('"', StringComparison.Ordinal)
            ? $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

    private static string ToHResultHex(int hresult) => $"0x{unchecked((uint)hresult):X8}";
}

internal readonly record struct PackagedActivationAttempt(
    int HResult,
    uint ProcessId,
    string Message,
    PackagedActivationDiagnosticStage Stage,
    bool TimedOut = false)
{
    public bool Succeeded => HResult >= 0;

    public static PackagedActivationAttempt Failure(
        int hresult,
        string message,
        PackagedActivationDiagnosticStage stage) => new(hresult, 0, message, stage);

    public static PackagedActivationAttempt Timeout(string message, PackagedActivationDiagnosticStage stage) => new(
        unchecked((int)0x800705B4),
        0,
        message,
        stage,
        TimedOut: true);
}

internal interface IApplicationActivationPlatform
{
    PackagedActivationAttempt ActivateApplicationOnDedicatedMta(string appUserModelId, string arguments, PackagedApplicationActivationContext context);
}

internal sealed class ApplicationActivationPlatform : IApplicationActivationPlatform
{
    private static readonly TimeSpan DiagnosticAttemptTimeout = TimeSpan.FromSeconds(5);

    public PackagedActivationAttempt ActivateApplicationOnDedicatedMta(string appUserModelId, string arguments, PackagedApplicationActivationContext context)
    {
        PackagedActivationAttempt? attempt = null;
        var currentStage = (int)PackagedActivationDiagnosticStage.CoInitializeEx;
        var thread = new Thread(() => attempt = ActivateApplicationCore(
            appUserModelId,
            arguments,
            context,
            stage => Volatile.Write(ref currentStage, (int)stage)))
        {
            IsBackground = true,
            Name = "TenchyShell packaged activation"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!thread.Join(DiagnosticAttemptTimeout))
        {
            return PackagedActivationAttempt.Timeout(
                $"La activación COM no completó dentro de {DiagnosticAttemptTimeout.TotalSeconds:0} s.",
                (PackagedActivationDiagnosticStage)Volatile.Read(ref currentStage));
        }

        return attempt ?? PackagedActivationAttempt.Failure(
            unchecked((int)0x80004005),
            "El hilo MTA de diagnóstico terminó sin resultado.",
            (PackagedActivationDiagnosticStage)Volatile.Read(ref currentStage));
    }

    private static PackagedActivationAttempt ActivateApplicationCore(
        string appUserModelId,
        string arguments,
        PackagedApplicationActivationContext context,
        Action<PackagedActivationDiagnosticStage>? stageChanged = null)
    {
        stageChanged?.Invoke(PackagedActivationDiagnosticStage.CoInitializeEx);
        using var apartment = ComApartmentScope.InitializeMta();
        if (!apartment.CanUseCom)
        {
            return PackagedActivationAttempt.Failure(
                apartment.HResult,
                DescribeHResult(apartment.HResult),
                PackagedActivationDiagnosticStage.CoInitializeEx);
        }

        stageChanged?.Invoke(PackagedActivationDiagnosticStage.CoCreateInstance);
        var classId = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        var interfaceId = new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");
        var createResult = NativeMethods.CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            context == PackagedApplicationActivationContext.InProcess
                ? NativeMethods.CLSCTX_INPROC_SERVER
                : NativeMethods.CLSCTX_LOCAL_SERVER,
            ref interfaceId,
            out var activationManagerObject);
        if (createResult < 0 || activationManagerObject is not IApplicationActivationManager manager)
        {
            return PackagedActivationAttempt.Failure(
                createResult,
                DescribeHResult(createResult),
                PackagedActivationDiagnosticStage.CoCreateInstance);
        }

        try
        {
            stageChanged?.Invoke(PackagedActivationDiagnosticStage.ActivateApplication);
            var activateResult = manager.ActivateApplication(appUserModelId, arguments, ActivateOptions.None, out var processId);
            return activateResult < 0
                ? PackagedActivationAttempt.Failure(
                    activateResult,
                    DescribeHResult(activateResult),
                    PackagedActivationDiagnosticStage.ActivateApplication)
                : new PackagedActivationAttempt(
                    activateResult,
                    processId,
                    "Activación aceptada por Windows.",
                    PackagedActivationDiagnosticStage.ActivateApplication);
        }
        catch (COMException exception)
        {
            return PackagedActivationAttempt.Failure(
                exception.HResult,
                exception.Message,
                PackagedActivationDiagnosticStage.ActivateApplication);
        }
        catch (Win32Exception exception)
        {
            return PackagedActivationAttempt.Failure(
                exception.NativeErrorCode,
                exception.Message,
                PackagedActivationDiagnosticStage.ActivateApplication);
        }
        catch (Exception exception)
        {
            return PackagedActivationAttempt.Failure(
                exception.HResult,
                exception.Message,
                PackagedActivationDiagnosticStage.ActivateApplication);
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static string DescribeHResult(int hresult) => hresult >= 0
        ? "Operación COM completada."
        : Marshal.GetExceptionForHR(hresult)?.Message ?? $"HRESULT 0x{unchecked((uint)hresult):X8}.";

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, ActivateOptions options, out uint processId);

        int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string protocol, IntPtr itemArray, out uint processId);
    }

    [Flags]
    private enum ActivateOptions
    {
        None = 0
    }
}

/// <summary>Equilibra CoInitializeEx/CoUninitialize dentro de la capa Win32.</summary>
internal sealed class ComApartmentScope : IDisposable
{
    private readonly bool uninitialize;
    private bool disposed;

    private ComApartmentScope(int hresult, bool uninitialize)
    {
        HResult = hresult;
        this.uninitialize = uninitialize;
    }

    public int HResult { get; }

    public bool CanUseCom => HResult >= 0 || HResult == NativeMethods.RPC_E_CHANGED_MODE;

    public static ComApartmentScope InitializeMta()
    {
        var result = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
        return new ComApartmentScope(result, result >= 0);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (uninitialize) NativeMethods.CoUninitialize();
        disposed = true;
    }
}
