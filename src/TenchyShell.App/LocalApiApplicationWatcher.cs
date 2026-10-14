using TenchyShell.Win32;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Runtime;
using TenchyShell.Core.Diagnostics;
using System.Diagnostics;

namespace TenchyShell.App;

internal sealed class LocalApiApplicationWatcher : IDisposable
{
    private readonly MessageLoopHost messageLoop;
    private readonly LocalApiStateProvider state;
    private readonly CoalescedRefreshScheduler refreshScheduler;
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly object metricsGate = new();
    private MetricsGroup? pendingMetrics;
    private MetricsGroup? activeMetrics;
    private long refreshStartedAt;
    private bool disposed;

    public LocalApiApplicationWatcher(MessageLoopHost messageLoop, LocalApiStateProvider state, ILogger logger,
        LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.messageLoop = messageLoop;
        this.state = state;
        this.benchmarkRecorder = benchmarkRecorder?.IsEnabled == true ? benchmarkRecorder : null;
        refreshScheduler = new CoalescedRefreshScheduler(messageLoop.TryPost, RefreshApplications,
            exception => logger.Error("Falló el refresco opcional de aplicaciones para la API local.", exception),
            this.benchmarkRecorder is null ? null : BeginRefreshMetrics,
            collectRequestCounts: this.benchmarkRecorder is not null);
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
            FileSystemEventHandler refresh = (_, args) => RequestRefresh(args.ChangeType);
            RenamedEventHandler renamed = (_, _) => RequestRefresh(WatcherChangeTypes.Renamed);
            watcher.Created += refresh;
            watcher.Deleted += refresh;
            watcher.Changed += refresh;
            watcher.Renamed += renamed;
            if (benchmarkRecorder is not null) watcher.Error += OnWatcherError;
            watchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        refreshScheduler.Dispose();
        MetricsGroup? unfinished;
        lock (metricsGate)
        {
            disposed = true;
            unfinished = pendingMetrics;
            pendingMetrics = null;
        }
        if (unfinished is not null) RecordMetrics(unfinished, "shutdown_pending", null, null);
    }

    private void RequestRefresh(WatcherChangeTypes changeType)
    {
        if (benchmarkRecorder is null)
        {
            refreshScheduler.Request();
            return;
        }

        var signaledAt = Stopwatch.GetTimestamp();
        refreshScheduler.Request((accepted, coalesced) =>
        {
            lock (metricsGate)
            {
                if (disposed) return;
                var group = pendingMetrics ??= new MetricsGroup();
                group.AddSignal(signaledAt);
                switch (changeType)
                {
                    case WatcherChangeTypes.Created: group.Created++; break;
                    case WatcherChangeTypes.Deleted: group.Deleted++; break;
                    case WatcherChangeTypes.Changed: group.Changed++; break;
                    case WatcherChangeTypes.Renamed: group.Renamed++; break;
                }
                group.Requests++;
                if (coalesced) group.CoalescedRequests++;
                if (!accepted) group.RejectedRequests++;
            }
        });
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        // Error details can contain user paths. Retain only the aggregate count.
        lock (metricsGate)
        {
            if (disposed) return;
            var group = pendingMetrics ??= new MetricsGroup();
            group.AddSignal(Stopwatch.GetTimestamp());
            group.WatcherErrors++;
        }
    }

    private void BeginRefreshMetrics()
    {
        // Called under the scheduler gate, before a new signal can be assigned.
        lock (metricsGate)
        {
            activeMetrics = pendingMetrics ?? new MetricsGroup();
            pendingMetrics = null;
            refreshStartedAt = Stopwatch.GetTimestamp();
        }
    }

    private void RefreshApplications()
    {
        if (benchmarkRecorder is null)
        {
            state.RefreshApplications();
            return;
        }

        var succeeded = false;
        try
        {
            state.RefreshApplications();
            succeeded = true;
        }
        finally
        {
            var completedAt = Stopwatch.GetTimestamp();
            MetricsGroup? group;
            lock (metricsGate)
            {
                group = activeMetrics;
                activeMetrics = null;
            }
            if (group is not null)
                RecordMetrics(group, succeeded ? "refresh_completed" : "refresh_failed", refreshStartedAt, completedAt);
        }
    }

    private void RecordMetrics(MetricsGroup group, string outcome, long? startedAt, long? completedAt)
    {
        benchmarkRecorder!.Record("application_catalog_watcher_metrics", new
        {
            outcome,
            createdEvents = group.Created,
            deletedEvents = group.Deleted,
            changedEvents = group.Changed,
            renamedEvents = group.Renamed,
            watcherErrors = group.WatcherErrors,
            requests = group.Requests,
            coalescedRequests = group.CoalescedRequests,
            rejectedRequests = group.RejectedRequests,
            refreshesExecuted = startedAt.HasValue ? 1 : 0,
            refreshesCompleted = outcome == "refresh_completed" ? 1 : 0,
            refreshDurationMs = startedAt.HasValue && completedAt.HasValue
                ? (double?)Stopwatch.GetElapsedTime(startedAt.Value, completedAt.Value).TotalMilliseconds : null,
            firstSignalToCompletionMs = completedAt.HasValue && group.FirstSignal.HasValue
                ? (double?)Stopwatch.GetElapsedTime(group.FirstSignal.Value, completedAt.Value).TotalMilliseconds : null,
            lastSignalToCompletionMs = completedAt.HasValue && group.LastSignal.HasValue
                ? (double?)Stopwatch.GetElapsedTime(group.LastSignal.Value, completedAt.Value).TotalMilliseconds : null
        });
    }

    private sealed class MetricsGroup
    {
        public long Created, Deleted, Changed, Renamed, WatcherErrors;
        public long Requests, CoalescedRequests, RejectedRequests;
        public long? FirstSignal, LastSignal;

        public void AddSignal(long timestamp)
        {
            FirstSignal = FirstSignal.HasValue ? Math.Min(FirstSignal.Value, timestamp) : timestamp;
            LastSignal = LastSignal.HasValue ? Math.Max(LastSignal.Value, timestamp) : timestamp;
        }
    }
}
