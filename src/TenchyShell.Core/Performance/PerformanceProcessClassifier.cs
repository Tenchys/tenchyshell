namespace TenchyShell.Core.Performance;

public enum PerformanceScenario
{
    TenchyShell,
    Explorer
}

public enum PerformancePhase
{
    Idle,
    CommonWorkflow,
    TenchyShellStress
}

public enum PerformanceProcessRole
{
    Shell,
    Tool
}

public static class PerformanceProcessClassifier
{
    private static readonly HashSet<string> TenchyShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TenchyShell",
        "MinimalShell"
    };

    private static readonly HashSet<string> CommonToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wezterm",
        "wezterm-gui",
        "yazi"
    };

    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "brave",
        "chrome",
        "firefox",
        "msedge",
        "opera"
    };

    public static bool IsIncludedRoot(PerformanceScenario scenario, string processName)
    {
        return ClassifyRoot(scenario, processName) is not null;
    }

    public static PerformanceProcessRole? ClassifyRoot(PerformanceScenario scenario, string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
        if (BrowserNames.Contains(normalized)) return null;
        if (CommonToolNames.Contains(normalized)) return PerformanceProcessRole.Tool;
        var isShell = scenario == PerformanceScenario.TenchyShell
            ? TenchyShellNames.Contains(normalized)
            : normalized.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        return isShell ? PerformanceProcessRole.Shell : null;
    }

    public static bool IsExplicitlyExcluded(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        return BrowserNames.Contains(Path.GetFileNameWithoutExtension(processName.Trim()));
    }
}
