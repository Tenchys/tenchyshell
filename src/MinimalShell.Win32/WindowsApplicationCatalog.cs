using System.Runtime.InteropServices;
using MinimalShell.Core.Applications;

namespace MinimalShell.Win32;

public sealed class WindowsApplicationCatalog : IApplicationCatalog
{
    private ApplicationSearchCatalog? catalog;

    public IReadOnlyList<ApplicationEntry> GetAll() => GetCatalog().GetAll();

    public IReadOnlyList<ApplicationEntry> Search(string query) => GetCatalog().Search(query);

    public void Refresh()
    {
        catalog = new ApplicationSearchCatalog(DiscoverApplications());
    }

    private ApplicationSearchCatalog GetCatalog() => catalog ??= new ApplicationSearchCatalog(DiscoverApplications());

    private static IEnumerable<ApplicationEntry> DiscoverApplications()
    {
        foreach (var entry in DiscoverStartMenuShortcuts())
        {
            yield return entry;
        }

        foreach (var entry in DiscoverShellApplications())
        {
            yield return entry;
        }
    }

    private static IEnumerable<ApplicationEntry> DiscoverStartMenuShortcuts()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);

                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return new ApplicationEntry(name, ApplicationActivationKind.Executable, file);
                }
            }
        }
    }

    private static IEnumerable<ApplicationEntry> DiscoverShellApplications()
    {
        var applications = new List<ApplicationEntry>();
        object? shell = null;
        object? folder = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");

            if (shellType is null)
            {
                return applications;
            }

            shell = Activator.CreateInstance(shellType);
            dynamic? dynamicShell = shell;
            folder = dynamicShell?.NameSpace("shell:AppsFolder");
            dynamic? dynamicFolder = folder;

            if (dynamicFolder is null)
            {
                return applications;
            }

            foreach (var item in dynamicFolder.Items())
            {
                string? name = null;
                string? path = null;
                string? appUserModelId = null;

                try
                {
                    name = item.Name as string;
                    path = item.Path as string;
                    appUserModelId = item.ExtendedProperty("System.AppUserModel.ID") as string;
                }
                catch
                {
                    // A shell item can disappear or expose incomplete metadata while enumerating.
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var target = BuildShellTarget(path, appUserModelId);

                if (target is null)
                {
                    continue;
                }

                var kind = target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                    ? ApplicationActivationKind.ShellApplication
                    : ApplicationActivationKind.Executable;

                applications.Add(new ApplicationEntry(name, kind, target, appUserModelId));
            }
        }
        catch
        {
            // Shell enumeration is optional; Start Menu shortcuts remain available if it fails.
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }

        return applications;
    }

    private static string? BuildShellTarget(string? path, string? appUserModelId)
    {
        if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (!string.IsNullOrWhiteSpace(appUserModelId))
        {
            return $"shell:AppsFolder\\{appUserModelId}";
        }

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
