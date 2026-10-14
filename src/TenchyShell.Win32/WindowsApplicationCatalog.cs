using System.Runtime.InteropServices;
using System.Diagnostics;
using TenchyShell.Core.Applications;

namespace TenchyShell.Win32;

public sealed class WindowsApplicationCatalog : IRefreshableApplicationCatalog
{
    private ApplicationSearchCatalog? catalog;

    public event EventHandler<ApplicationCatalogRefreshMetrics>? Refreshed;

    public IReadOnlyList<ApplicationEntry> GetAll() => GetCatalog().GetAll();

    public IReadOnlyList<ApplicationEntry> Search(string query) => GetCatalog().Search(query);

    public void Refresh()
        => Refresh("refresh");

    public void Refresh(string source)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var shellAttempts = 0;
        var shellFailures = 0;
        var startMenu = DiscoverStartMenuShortcuts().ToArray();
        var comStartedAt = Stopwatch.GetTimestamp();
        var shell = DiscoverShellApplications(ref shellAttempts, ref shellFailures);
        var all = startMenu.Concat(shell).ToArray();
        catalog = new ApplicationSearchCatalog(all);
        Refreshed?.Invoke(this, new ApplicationCatalogRefreshMetrics(
            string.IsNullOrWhiteSpace(source) ? "refresh" : source,
            startMenu.Length,
            shell.Count,
            catalog.GetAll().Count,
            shellAttempts,
            shellFailures,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            Stopwatch.GetElapsedTime(comStartedAt).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
    }

    private ApplicationSearchCatalog GetCatalog()
    {
        if (catalog is null)
        {
            Refresh();
        }
        return catalog!;
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

    private static List<ApplicationEntry> DiscoverShellApplications(ref int attempts, ref int failures)
    {
        attempts++;
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
            failures++;
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
