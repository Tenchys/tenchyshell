namespace TenchyShell.Win32;

public readonly record struct HotkeyCombination(uint Modifiers, uint VirtualKey);

public static class HotkeyParser
{
    private static readonly Dictionary<string, uint> ModifierValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = NativeMethods.MOD_CONTROL,
        ["CONTROL"] = NativeMethods.MOD_CONTROL,
        ["ALT"] = NativeMethods.MOD_ALT,
        ["SHIFT"] = NativeMethods.MOD_SHIFT,
        ["WIN"] = NativeMethods.MOD_WIN,
        ["WINDOWS"] = NativeMethods.MOD_WIN
    };

    public static bool TryParse(string value, out HotkeyCombination combination, out string error)
    {
        combination = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "El hotkey está vacío.";
            return false;
        }

        var modifiers = 0u;
        var virtualKey = 0u;

        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToUpperInvariant();

            if (ModifierValues.TryGetValue(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (virtualKey != 0 || !TryParseKey(part, out virtualKey))
            {
                error = $"La tecla '{rawPart}' no es válida o está repetida.";
                return false;
            }
        }

        if (virtualKey == 0)
        {
            error = $"El hotkey '{value}' debe contener una tecla válida.";
            return false;
        }

        if (modifiers == 0 && !IsFunctionKey(virtualKey) && virtualKey != NativeMethods.VK_TAB)
        {
            error = $"El hotkey '{value}' debe contener modificadores, salvo Tab y las teclas de función F1 a F24.";
            return false;
        }

        combination = new HotkeyCombination(modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        return true;
    }

    private static bool TryParseKey(string value, out uint virtualKey)
    {
        if (value.Length == 1 && char.IsLetter(value[0]))
        {
            virtualKey = value[0];
            return true;
        }

        if (value.Length == 1 && char.IsDigit(value[0]))
        {
            virtualKey = value[0];
            return true;
        }

        if (value.Equals("SPACE", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = 0x20;
            return true;
        }

        if (value.Equals("ENTER", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = 0x0D;
            return true;
        }

        if (value.Equals("TAB", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = NativeMethods.VK_TAB;
            return true;
        }

        var namedKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28
        };

        if (namedKeys.TryGetValue(value, out virtualKey))
        {
            return true;
        }

        if (value.Length >= 2 && value[0] == 'F' && int.TryParse(value[1..], out var functionNumber) && functionNumber is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionNumber - 1);
            return true;
        }

        virtualKey = 0;
        return false;
    }

    private static bool IsFunctionKey(uint virtualKey) => virtualKey is >= 0x70 and <= 0x87;
}
