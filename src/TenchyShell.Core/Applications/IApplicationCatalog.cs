namespace TenchyShell.Core.Applications;

public interface IApplicationCatalog
{
    IReadOnlyList<ApplicationEntry> GetAll();

    IReadOnlyList<ApplicationEntry> Search(string query);
}

public interface IRefreshableApplicationCatalog : IApplicationCatalog
{
    void Refresh();
    void Refresh(string source);
    event EventHandler<ApplicationCatalogRefreshMetrics>? Refreshed;
}

public sealed record ApplicationCatalogRefreshMetrics(
    string Source,
    int StartMenuEntries,
    int ShellEntries,
    int TotalEntries,
    int ComEnumerationAttempts,
    int ComEnumerationFailures,
    double DurationMilliseconds,
    double ComDurationMilliseconds,
    long AllocatedBytes);
