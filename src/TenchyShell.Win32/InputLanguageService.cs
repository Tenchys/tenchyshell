using System.Globalization;
using System.Runtime.InteropServices;
using TenchyShell.Core.InputLanguages;

namespace TenchyShell.Win32;

/// <summary>Adaptador Win32 para los métodos de entrada de la sesión actual.</summary>
public sealed class InputLanguageService : IInputLanguageService
{
    public InputLanguageSnapshot GetSnapshot(nint targetWindow)
    {
        try
        {
            var count = NativeMethods.GetKeyboardLayoutList(0, Array.Empty<IntPtr>());
            if (count <= 0)
            {
                return new InputLanguageSnapshot(Array.Empty<InputLanguage>(), IntPtr.Zero, "Windows no devolvió métodos de entrada.");
            }

            var handles = new IntPtr[count];
            var returned = NativeMethods.GetKeyboardLayoutList(handles.Length, handles);
            var activeHandle = GetActiveLayout(targetWindow);
            var languages = handles
                .Take(Math.Max(0, returned))
                .Append(activeHandle)
                .Where(handle => handle != IntPtr.Zero)
                .Distinct()
                .Select(CreateLanguage)
                .OrderBy(language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            return new InputLanguageSnapshot(languages, activeHandle, null);
        }
        catch (Exception exception)
        {
            return new InputLanguageSnapshot(Array.Empty<InputLanguage>(), IntPtr.Zero, exception.Message);
        }
    }

    public InputLanguageOperationResult Activate(nint layoutHandle, nint targetWindow)
    {
        if (layoutHandle == IntPtr.Zero) return InputLanguageOperationResult.Failure("El método de entrada no es válido.");
        if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
        {
            return InputLanguageOperationResult.Failure("La ventana que tenía el foco ya no está disponible.");
        }

        if (!NativeMethods.PostMessage(targetWindow, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layoutHandle))
        {
            return InputLanguageOperationResult.Failure($"Windows rechazó la solicitud de cambio de idioma ({Marshal.GetLastWin32Error()}).");
        }

        return InputLanguageOperationResult.Success();
    }

    private static IntPtr GetActiveLayout(nint targetWindow)
    {
        var window = targetWindow != IntPtr.Zero && NativeMethods.IsWindow(targetWindow)
            ? targetWindow
            : NativeMethods.GetForegroundWindow();
        var threadId = window == IntPtr.Zero ? 0u : NativeMethods.GetWindowThreadProcessId(window, out _);
        return NativeMethods.GetKeyboardLayout(threadId);
    }

    private static InputLanguage CreateLanguage(IntPtr handle)
    {
        var languageId = unchecked((ushort)handle.ToInt64());
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageId);
            var shortName = culture.TwoLetterISOLanguageName.ToUpperInvariant();
            var layout = $"{handle.ToInt64() & 0xffffffff:X8}";
            return new InputLanguage(handle, shortName, culture.DisplayName, $"{culture.DisplayName} · layout {layout}");
        }
        catch (CultureNotFoundException)
        {
            var layout = $"{handle.ToInt64() & 0xffffffff:X8}";
            return new InputLanguage(handle, "?", $"Método de entrada {layout}", $"Identificador de layout {layout}");
        }
    }
}
