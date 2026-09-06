namespace TenchyShell.Core.LocalApi;

public enum LocalApiCapabilityKind
{
    Query,
    Command,
    Subscription
}

public sealed record LocalApiCapability(string Name, LocalApiCapabilityKind Kind);

public static class LocalApiCapabilityCatalog
{
    public static IReadOnlyList<LocalApiCapability> Capabilities { get; } = Array.AsReadOnly(new[]
    {
        new LocalApiCapability("workspace.active.get", LocalApiCapabilityKind.Query),
        new LocalApiCapability("workspaces.get", LocalApiCapabilityKind.Query),
        new LocalApiCapability("applications.get", LocalApiCapabilityKind.Query),
        new LocalApiCapability("windows.get", LocalApiCapabilityKind.Query),
        new LocalApiCapability("workspace.switch", LocalApiCapabilityKind.Command),
        new LocalApiCapability("window.active.move", LocalApiCapabilityKind.Command),
        new LocalApiCapability("subscribe", LocalApiCapabilityKind.Subscription)
    });

    public static IReadOnlyList<string> Queries { get; } = Capabilities
        .Where(capability => capability.Kind == LocalApiCapabilityKind.Query)
        .Select(capability => capability.Name)
        .ToArray().AsReadOnly();

    public static IReadOnlyList<string> Commands { get; } = Capabilities
        .Where(capability => capability.Kind == LocalApiCapabilityKind.Command)
        .Select(capability => capability.Name)
        .ToArray().AsReadOnly();

    public static IReadOnlyList<string> Topics { get; } = Array.AsReadOnly(new[] { "workspace", "windows", "applications" });
}
