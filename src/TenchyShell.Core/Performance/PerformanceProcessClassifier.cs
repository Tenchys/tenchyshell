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
    Core,
    Auxiliary,
    Tool
}

public sealed record PerformanceProcessDescriptor(
    int Id,
    int ParentId,
    string Name,
    int SessionId = 0,
    DateTimeOffset? StartedAt = null,
    bool HasExited = false);

public static class PerformanceProcessClassifier
{
    private static readonly HashSet<string> TenchyShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TenchyShell",
        "MinimalShell"
    };

    private static readonly HashSet<string> AuxiliaryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TenchyShell.NotificationBridge",
        "TenchyShellNotificationBridge"
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
        var isCoreCandidate = scenario == PerformanceScenario.TenchyShell
            ? TenchyShellNames.Contains(normalized)
            : normalized.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        return isCoreCandidate ? PerformanceProcessRole.Core : null;
    }

    /// <summary>
    /// Clasifica una instantánea usando el PID entregado por el orquestador como
    /// única identidad del núcleo. El nombre solo valida ese PID; nunca permite
    /// convertir una segunda instancia de TenchyShell en otro núcleo.
    /// </summary>
    public static IReadOnlyDictionary<int, PerformanceProcessRole> ClassifyProcesses(
        PerformanceScenario scenario,
        int mainProcessId,
        int observerProcessId,
        IEnumerable<PerformanceProcessDescriptor> processes,
        int? measurementSessionId = null,
        DateTimeOffset? expectedMainStartTime = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (mainProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(mainProcessId));
        if (observerProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(observerProcessId));
        if (mainProcessId == observerProcessId)
        {
            throw new ArgumentException("El observador no puede ser el núcleo medido.", nameof(observerProcessId));
        }

        var snapshot = processes.ToArray();
        var duplicate = snapshot.GroupBy(process => process.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"El PID {duplicate.Key} aparece duplicado en la instantánea.", nameof(processes));
        }

        var main = snapshot.SingleOrDefault(process => process.Id == mainProcessId)
            ?? throw new ArgumentException("El PID principal no existe en la instantánea.", nameof(mainProcessId));
        if (!IsExpectedCoreName(scenario, main.Name))
        {
            throw new ArgumentException("El PID principal no pertenece al escenario solicitado.", nameof(mainProcessId));
        }
        if (main.HasExited)
        {
            throw new ArgumentException("El PID principal ya finalizó.", nameof(mainProcessId));
        }
        if (measurementSessionId.HasValue && main.SessionId != measurementSessionId.Value)
        {
            throw new ArgumentException("El PID principal pertenece a otra sesión.", nameof(mainProcessId));
        }
        if (expectedMainStartTime.HasValue && main.StartedAt != expectedMainStartTime.Value)
        {
            throw new ArgumentException("El PID principal fue reutilizado.", nameof(mainProcessId));
        }

        var roles = new Dictionary<int, PerformanceProcessRole>
        {
            [mainProcessId] = PerformanceProcessRole.Core
        };

        if (scenario == PerformanceScenario.TenchyShell)
        {
            foreach (var process in snapshot)
            {
                if (process.Id != observerProcessId && process.Id != mainProcessId && IsExplicitAuxiliary(process.Name))
                {
                    roles[process.Id] = PerformanceProcessRole.Auxiliary;
                }
            }
            PropagateDescendants(snapshot, roles, observerProcessId, PerformanceProcessRole.Core, PerformanceProcessRole.Auxiliary);
            PropagateDescendants(snapshot, roles, observerProcessId, PerformanceProcessRole.Auxiliary, PerformanceProcessRole.Auxiliary);
        }

        foreach (var process in snapshot)
        {
            if (process.Id == observerProcessId || roles.ContainsKey(process.Id)) continue;
            if (CommonToolNames.Contains(Normalize(process.Name))) roles[process.Id] = PerformanceProcessRole.Tool;
        }
        PropagateDescendants(snapshot, roles, observerProcessId, PerformanceProcessRole.Tool, PerformanceProcessRole.Tool);

        roles.Remove(observerProcessId);
        return roles;
    }

    public static bool IsExplicitlyExcluded(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        return BrowserNames.Contains(Path.GetFileNameWithoutExtension(processName.Trim()));
    }

    private static bool IsExpectedCoreName(PerformanceScenario scenario, string processName)
    {
        var normalized = Normalize(processName);
        return scenario == PerformanceScenario.TenchyShell
            ? TenchyShellNames.Contains(normalized)
            : normalized.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitAuxiliary(string processName)
    {
        var normalized = Normalize(processName);
        return TenchyShellNames.Contains(normalized) || AuxiliaryNames.Contains(normalized);
    }

    private static string Normalize(string processName) =>
        string.IsNullOrWhiteSpace(processName)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(processName.Trim());

    private static void PropagateDescendants(
        IReadOnlyList<PerformanceProcessDescriptor> processes,
        IDictionary<int, PerformanceProcessRole> roles,
        int observerProcessId,
        PerformanceProcessRole parentRole,
        PerformanceProcessRole childRole)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var process in processes)
            {
                if (process.Id == observerProcessId || roles.ContainsKey(process.Id)) continue;
                if (roles.TryGetValue(process.ParentId, out var role) && role == parentRole)
                {
                    roles[process.Id] = childRole;
                    changed = true;
                }
            }
        }
    }
}
