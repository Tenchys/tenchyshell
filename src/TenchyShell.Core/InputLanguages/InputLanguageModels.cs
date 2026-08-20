namespace TenchyShell.Core.InputLanguages;

public sealed record InputLanguage(nint Handle, string ShortName, string DisplayName, string Tooltip);

public sealed record InputLanguageSnapshot(
    IReadOnlyList<InputLanguage> Languages,
    nint ActiveHandle,
    string? Error)
{
    public InputLanguage? ActiveLanguage => Languages.FirstOrDefault(language => language.Handle == ActiveHandle);
}

public sealed record InputLanguageOperationResult(bool Succeeded, string? Error)
{
    public static InputLanguageOperationResult Success() => new(true, null);

    public static InputLanguageOperationResult Failure(string error) => new(false, error);
}

public static class InputLanguageLabel
{
    public static string Format(string shortName, string displayName, string format) =>
        string.Equals(format, "full", StringComparison.OrdinalIgnoreCase)
            ? displayName
            : shortName;
}
