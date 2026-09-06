namespace TenchyShell.Core.Applications;

/// <summary>
/// Argumentos de la prueba de activación MSIX/UWP. No contiene configuración
/// persistente: los AppUserModelId solo existen durante la ejecución solicitada.
/// </summary>
public sealed record PackagedActivationDiagnosticOptions(
    IReadOnlyList<string> AppUserModelIds,
    PackagedApplicationActivationContextSelection ContextSelection)
{
    public IEnumerable<PackagedApplicationActivationContext> Contexts => ContextSelection switch
    {
        PackagedApplicationActivationContextSelection.InProcess => [PackagedApplicationActivationContext.InProcess],
        PackagedApplicationActivationContextSelection.LocalServer => [PackagedApplicationActivationContext.LocalServer],
        _ => [PackagedApplicationActivationContext.InProcess, PackagedApplicationActivationContext.LocalServer]
    };

    public static bool TryParse(
        IReadOnlyList<string> args,
        out PackagedActivationDiagnosticOptions? options,
        out string? error)
    {
        var appUserModelIds = new List<string>();
        var contextSelection = PackagedApplicationActivationContextSelection.Both;
        var contextSeen = false;
        var diagnosticSeen = false;

        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].Equals("--diagnose-packaged", StringComparison.OrdinalIgnoreCase))
            {
                diagnosticSeen = true;
                if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) ||
                    args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options = null;
                    error = "--diagnose-packaged requiere un AppUserModelId no vacío.";
                    return false;
                }

                appUserModelIds.Add(args[++index]);
                continue;
            }

            if (!args[index].Equals("--activation-context", StringComparison.OrdinalIgnoreCase)) continue;

            if (contextSeen)
            {
                options = null;
                error = "La opción --activation-context no puede repetirse.";
                return false;
            }

            contextSeen = true;
            if (index + 1 >= args.Count || !TryParseContext(args[++index], out contextSelection))
            {
                options = null;
                error = "--activation-context requiere in-process, local-server o both.";
                return false;
            }
        }

        if (!diagnosticSeen)
        {
            options = null;
            error = contextSeen ? "--activation-context requiere al menos una opción --diagnose-packaged." : null;
            return !contextSeen;
        }

        options = new PackagedActivationDiagnosticOptions(appUserModelIds, contextSelection);
        error = null;
        return true;
    }

    private static bool TryParseContext(string value, out PackagedApplicationActivationContextSelection selection)
    {
        if (value.Equals("in-process", StringComparison.OrdinalIgnoreCase))
        {
            selection = PackagedApplicationActivationContextSelection.InProcess;
            return true;
        }

        if (value.Equals("local-server", StringComparison.OrdinalIgnoreCase))
        {
            selection = PackagedApplicationActivationContextSelection.LocalServer;
            return true;
        }

        if (value.Equals("both", StringComparison.OrdinalIgnoreCase))
        {
            selection = PackagedApplicationActivationContextSelection.Both;
            return true;
        }

        selection = default;
        return false;
    }
}
