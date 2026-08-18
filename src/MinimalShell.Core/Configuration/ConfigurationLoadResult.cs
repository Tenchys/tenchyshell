namespace MinimalShell.Core.Configuration;

public sealed class ConfigurationLoadResult
{
    private ConfigurationLoadResult(
        ShellConfiguration configuration,
        IReadOnlyList<string> errors,
        bool usedDefaults)
    {
        Configuration = configuration;
        Errors = errors;
        UsedDefaults = usedDefaults;
    }

    public ShellConfiguration Configuration { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public bool UsedDefaults { get; }

    public static ConfigurationLoadResult Success(ShellConfiguration configuration, bool usedDefaults) =>
        new(configuration, Array.Empty<string>(), usedDefaults);

    public static ConfigurationLoadResult Invalid(ShellConfiguration configuration, IReadOnlyList<string> errors) =>
        new(configuration, errors, usedDefaults: false);
}
