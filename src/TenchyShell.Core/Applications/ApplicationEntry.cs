namespace TenchyShell.Core.Applications;

public sealed class ApplicationEntry
{
    public ApplicationEntry(
        string name,
        ApplicationActivationKind activationKind,
        string target,
        string? appUserModelId = null,
        IEnumerable<string>? arguments = null)
    {
        Name = name;
        DisplayName = name;
        ActivationKind = activationKind;
        Target = target;
        AppUserModelId = appUserModelId;
        Arguments = (arguments ?? Array.Empty<string>()).ToArray();
    }

    public string Name { get; }

    public string DisplayName { get; }

    public ApplicationActivationKind ActivationKind { get; }

    public string Target { get; }

    public string? AppUserModelId { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string DeduplicationKey =>
        $"{DisplayName.Trim()}\u001f{ActivationKind}\u001f{Target.Trim()}";
}
