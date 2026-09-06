namespace TenchyShell.Core.Configuration;

/// <summary>
/// Resuelve qué superficies opcionales se crearán para una sesión.
/// </summary>
public sealed record OptionalComponentsSelection(
    bool DockEnabled,
    bool ApplicationSearchEnabled,
    string? DockDisabledReason,
    string? ApplicationSearchDisabledReason)
{
    public static OptionalComponentsSelection From(ShellConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var dockReasons = new List<string>();
        if (!configuration.Optionals.Dock)
        {
            dockReasons.Add("OPTIONALS.dock = false");
        }

        if (!configuration.StatusPanel.Enabled)
        {
            dockReasons.Add("status_panel.enabled = false");
        }

        var applicationSearchReasons = new List<string>();
        if (!configuration.Optionals.ApplicationSearch)
        {
            applicationSearchReasons.Add("OPTIONALS.application_search = false");
        }

        if (!configuration.Launcher.Enabled)
        {
            applicationSearchReasons.Add("launcher.enabled = false");
        }

        return new OptionalComponentsSelection(
            dockReasons.Count == 0,
            applicationSearchReasons.Count == 0,
            BuildReason(dockReasons),
            BuildReason(applicationSearchReasons));
    }

    private static string? BuildReason(IReadOnlyList<string> reasons) =>
        reasons.Count == 0
            ? null
            : string.Join("; ", reasons) + ".";
}
