namespace MinimalShell.Core.Applications;

public interface IApplicationCatalog
{
    IReadOnlyList<ApplicationEntry> GetAll();

    IReadOnlyList<ApplicationEntry> Search(string query);
}
