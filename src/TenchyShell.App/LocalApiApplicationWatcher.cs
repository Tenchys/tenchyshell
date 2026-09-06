using TenchyShell.Win32;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Runtime;

namespace TenchyShell.App;

internal sealed class LocalApiApplicationWatcher : IDisposable
{
    private readonly MessageLoopHost messageLoop;
    private readonly LocalApiStateProvider state;
    private readonly CoalescedRefreshScheduler refreshScheduler;
    private readonly List<FileSystemWatcher> watchers = new();

    public LocalApiApplicationWatcher(MessageLoopHost messageLoop, LocalApiStateProvider state, ILogger logger)
    {
        this.messageLoop = messageLoop;
        this.state = state;
        refreshScheduler = new CoalescedRefreshScheduler(messageLoop.TryPost, state.RefreshApplications,
            exception => logger.Error("Falló el refresco opcional de aplicaciones para la API local.", exception));
    }

    public void Start()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler refresh = (_, _) => refreshScheduler.Request();
            RenamedEventHandler renamed = (_, _) => refreshScheduler.Request();
            watcher.Created += refresh;
            watcher.Deleted += refresh;
            watcher.Changed += refresh;
            watcher.Renamed += renamed;
            watchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        refreshScheduler.Dispose();
    }
}
