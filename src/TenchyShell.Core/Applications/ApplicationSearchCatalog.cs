namespace TenchyShell.Core.Applications;

public sealed class ApplicationSearchCatalog : IApplicationCatalog
{
    private readonly IReadOnlyList<ApplicationEntry> applications;

    public ApplicationSearchCatalog(IEnumerable<ApplicationEntry> applications)
    {
        this.applications = applications
            .Where(IsValid)
            .GroupBy(application => application.DeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ApplicationEntry> GetAll() => applications;

    public IReadOnlyList<ApplicationEntry> Search(string query)
    {
        var normalizedQuery = query.Trim();

        if (normalizedQuery.Length == 0)
        {
            return applications;
        }

        return applications
            .Where(application => application.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(application => application.DisplayName.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsValid(ApplicationEntry application) =>
        !string.IsNullOrWhiteSpace(application.DisplayName) &&
        !string.IsNullOrWhiteSpace(application.Target);
}
