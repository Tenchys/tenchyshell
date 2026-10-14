using System.Reflection;
using TenchyShell.Core.Logging;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class FileLoggerTests
{
    [Fact]
    public void WritesHistoricalPrefixAndSessionId()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory, sessionId: "session-42");

            logger.Error("mensaje histórico");

            var line = Assert.Single(File.ReadAllLines(logger.LogFilePath));
            Assert.StartsWith("20", line, StringComparison.Ordinal);
            Assert.Contains(" [ERROR] mensaje histórico ", line, StringComparison.Ordinal);
            Assert.EndsWith("[sessionId=session-42]", line, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void PerformsInitialMaintenanceAndKeepsUnrelatedFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var expired = Path.Combine(directory, "tenchyshell-20200101-000000.log");
            var unrelated = Path.Combine(directory, "unrelated.log");
            File.WriteAllText(expired, "expired");
            File.SetLastWriteTime(expired, DateTime.Now.AddDays(-30));
            File.WriteAllText(unrelated, "keep");

            _ = new FileLogger(directory, maximumAge: TimeSpan.FromDays(14));

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void RotatesAfterSizeThresholdAndScansOnlyWhenRotationIsNeeded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory, maximumBytes: 1) { MetricsEnabled = true };

            logger.Info("first");
            logger.Info("second");
            logger.Info("third");

            var metrics = logger.GetMetricsSnapshot();
            Assert.Equal(3, metrics.LogCalls);
            Assert.Equal(2, metrics.MaintenanceCalls);
            Assert.Equal(2, metrics.ArchiveScans);
            Assert.Equal(2, metrics.RotationsBySize);
            Assert.Equal(2, metrics.ArchiveFilesSeen);
            Assert.Equal(0, metrics.ArchiveCountAtEnd);
            Assert.Contains("third", File.ReadAllText(logger.LogFilePath), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void RetainsArchivesWithinAgeAndCumulativeStorageLimit()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var active = Path.Combine(directory, "tenchyshell.log");
            var newest = Path.Combine(directory, "tenchyshell-20260918-120001.log");
            var oldest = Path.Combine(directory, "tenchyshell-20260918-120000.log");
            File.WriteAllText(active, "1234");
            File.WriteAllText(newest, "5678");
            File.WriteAllText(oldest, "90ab");
            File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddMinutes(-2));

            _ = new FileLogger(directory, maximumBytes: 10, maximumAge: TimeSpan.FromDays(14));

            Assert.True(File.Exists(active));
            Assert.True(File.Exists(newest));
            Assert.False(File.Exists(oldest));
            var totalBytes = Directory.EnumerateFiles(directory, "tenchyshell*.log")
                .Sum(path => new FileInfo(path).Length);
            Assert.True(totalBytes <= 10);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void HandlesTimestampCollisionWithSuffixWithoutLosingActiveLog()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory);
            File.WriteAllText(logger.LogFilePath, "previous");
            File.SetLastWriteTime(logger.LogFilePath, DateTime.Now.AddDays(-1));

            var now = DateTimeOffset.Now;
            var createArchivePath = typeof(FileLogger).GetMethod(
                "CreateArchivePath",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var collisionPath = (string)createArchivePath.Invoke(logger, new object[] { now })!;
            File.WriteAllText(collisionPath, "collision");

            var maintainFiles = typeof(FileLogger).GetMethod(
                "MaintainFiles",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            maintainFiles.Invoke(logger, new object[] { now });

            Assert.True(File.Exists(collisionPath));
            var suffixedPath = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(logger.LogFilePath)}-{now:yyyyMMdd-HHmmssfff}-1{Path.GetExtension(logger.LogFilePath)}");
            Assert.True(File.Exists(suffixedPath));
            Assert.False(File.Exists(logger.LogFilePath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ReportsArchiveDeletionAndP95WhenMaintenanceRunsWithMetrics()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory, maximumBytes: 1) { MetricsEnabled = true };
            logger.Info("first");

            var expired = Path.Combine(directory, "tenchyshell-expired.log");
            File.WriteAllText(expired, "expired");
            File.SetLastWriteTime(expired, DateTime.Now.AddDays(-30));
            logger.Info("second");

            var metrics = logger.GetMetricsSnapshot();
            Assert.True(metrics.ArchiveScans >= 1);
            Assert.True(metrics.ArchiveFilesSeen >= 2);
            Assert.True(metrics.ArchiveFilesDeleted >= 1);
            Assert.True(metrics.WriteDurationP95Milliseconds >= 0);
            Assert.True(metrics.MaintenanceDurationP95Milliseconds >= 0);
            Assert.Equal(0, metrics.WriteFailures);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task RecordsContentionWithoutBlockingTheWriterForever()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory) { MetricsEnabled = true };
            var syncRoot = typeof(FileLogger)
                .GetField("syncRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(logger)!;
            Monitor.Enter(syncRoot);
            Task? write = null;

            // Give the worker a chance to reach the logger lock while the
            // test owns it. The bounded wait keeps this independent of I/O
            // speed and the lock is always released in finally.
            try
            {
                write = Task.Run(() => logger.Info("contended"));
                Thread.Sleep(500);
            }
            finally
            {
                Monitor.Exit(syncRoot);
            }

            Assert.NotNull(write);
            await write!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, logger.GetMetricsSnapshot().LogCalls);
            Assert.Equal(1, logger.GetMetricsSnapshot().ContentionCount);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ContinuesWhenTheLogDirectoryDisappears()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory) { MetricsEnabled = true };
            Directory.Delete(directory);

            var exception = Record.Exception(() => logger.Error("directory unavailable"));

            Assert.Null(exception);
            var metrics = logger.GetMetricsSnapshot();
            Assert.Equal(1, metrics.LogCalls);
            Assert.Equal(1, metrics.WriteFailures);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ContinuesWhenTheActiveLogPathIsNotWritableAsAFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "tenchyshell.log"));
            var logger = new FileLogger(directory) { MetricsEnabled = true };

            var exception = Record.Exception(() => logger.Info("active path unavailable"));

            Assert.Null(exception);
            Assert.Equal(1, logger.GetMetricsSnapshot().WriteFailures);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ContinuesWhenTheActiveLogIsLocked()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory, maximumBytes: 1) { MetricsEnabled = true };
            logger.Info("initial");

            using var stream = new FileStream(
                logger.LogFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var exception = Record.Exception(() => logger.Info("blocked active log"));

            Assert.Null(exception);
            Assert.True(File.Exists(logger.LogFilePath));
            Assert.Equal(1, logger.GetMetricsSnapshot().WriteFailures);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ContinuesWhenAnArchivedLogIsLocked()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory, maximumBytes: 1) { MetricsEnabled = true };
            logger.Info("initial");

            var lockedArchive = Path.Combine(directory, "tenchyshell-locked.log");
            File.WriteAllText(lockedArchive, "locked");
            using var stream = new FileStream(
                lockedArchive,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var exception = Record.Exception(() => logger.Info("locked archive"));

            Assert.Null(exception);
            Assert.True(File.Exists(lockedArchive));
            Assert.True(logger.GetMetricsSnapshot().ArchiveFilesSeen >= 1);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TenchyShell.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
