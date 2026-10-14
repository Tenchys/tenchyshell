using System.Text;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Tui;
using TenchyShell.Core.Yazi;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class TuiTests
{
    [Fact]
    public void TuiArgumentParserRequiresExactlyOneTomlPath()
    {
        Assert.True(TuiArgumentParser.TryGetConfigurationPath(
            new[] { "--tui", "settings.toml" }, out var path, out var error));
        Assert.Equal("settings.toml", path);
        Assert.Null(error);
        Assert.False(TuiArgumentParser.TryGetConfigurationPath(
            new[] { "--tui" }, out _, out error));
        Assert.Contains("ruta", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TuiMenuWrapsSelectionInBothDirections()
    {
        var menu = new TuiMenu(new[]
        {
            new TuiMenuItem("one", "Uno"),
            new TuiMenuItem("two", "Dos")
        });

        menu.MovePrevious();
        Assert.Equal("two", menu.SelectedItem!.Id);
        menu.MoveNext();
        menu.MoveNext();
        Assert.Equal("two", menu.SelectedItem!.Id);
    }

    [Fact]
    public void TuiListViewportKeepsSelectionVisibleAndSupportsPages()
    {
        var viewport = new TuiListViewport();
        viewport.SetItems(10, 3, resetSelection: true);

        viewport.Move(2);
        Assert.Equal(2, viewport.SelectedIndex);
        Assert.Equal(0, viewport.Offset);
        viewport.Move(1);
        Assert.Equal(1, viewport.Offset);
        Assert.Equal("Atajos 2–4 de 10", viewport.RangeText());

        viewport.PageDown();
        Assert.Equal(6, viewport.SelectedIndex);
        Assert.Equal(4, viewport.Offset);
        viewport.Home();
        Assert.Equal(0, viewport.SelectedIndex);
        Assert.Equal(0, viewport.Offset);
        viewport.End();
        Assert.Equal(9, viewport.SelectedIndex);
        Assert.Equal(7, viewport.Offset);
    }

    [Fact]
    public void TuiListViewportNormalizesEmptyAndResizedLists()
    {
        var viewport = new TuiListViewport();
        viewport.SetItems(0, 4, resetSelection: true);
        Assert.Equal(-1, viewport.SelectedIndex);
        Assert.Empty(viewport.VisibleIndexes());

        viewport.SetItems(20, 5, resetSelection: true);
        viewport.End();
        viewport.SetItems(20, 2);
        Assert.InRange(viewport.SelectedIndex, 0, 19);
        Assert.InRange(viewport.Offset, 0, 18);
        Assert.Contains(viewport.SelectedIndex, viewport.VisibleIndexes());
    }

    [Fact]
    public void TelemetrySummaryScriptLocatorFindsSiblingAndDevelopmentScripts()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configurationDirectory = Path.Combine(directory, "config");
            var siblingScripts = Path.Combine(configurationDirectory, "scripts");
            Directory.CreateDirectory(siblingScripts);
            var siblingScript = Path.Combine(siblingScripts, TelemetrySummaryScriptLocator.ScriptFileName);
            File.WriteAllText(siblingScript, "# test");
            var configurationPath = Path.Combine(configurationDirectory, "TenchyShell.toml");

            Assert.Equal(siblingScript, TelemetrySummaryScriptLocator.Find(configurationPath, "C:\\missing\\TenchyShell.exe", "C:\\missing"));

            File.Delete(siblingScript);
            var developmentScripts = Path.Combine(directory, "scripts");
            Directory.CreateDirectory(developmentScripts);
            var developmentScript = Path.Combine(developmentScripts, TelemetrySummaryScriptLocator.ScriptFileName);
            File.WriteAllText(developmentScript, "# test");

            Assert.Equal(developmentScript, TelemetrySummaryScriptLocator.Find(configurationPath, "C:\\missing\\TenchyShell.exe", "C:\\missing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void YaziViewScrollsReloadsAndExitsWithRedirectedAnsiInput()
    {
        var generation = 0;
        YaziShortcutReadResult ReadResult()
        {
            generation++;
            var shortcuts = Enumerable.Range(1, generation == 1 ? 20 : 3)
                .Select(index => new YaziShortcut("mgr", "keymap", $"k{index}", $"run{index}", $"desc{index}"))
                .ToArray();
            return new(new("C:\\yazi", "C:\\yazi\\keymap.toml", true), YaziShortcutLoadStatus.Loaded, shortcuts, Array.Empty<string>());
        }

        using var input = new StringReader("\x1b[B\x1b[B\x1b[B\x1b[B\r\x1b[6~r\x1b\x1b");
        using var output = new StringWriter();
        var tui = new TuiApplication(
            ShellConfiguration.CreateDefault(), Array.Empty<string>(), "config.toml", new NullLogger(), input, output,
            yaziReader: ReadResult, widthProvider: () => 80, heightProvider: () => 15);

        Assert.Equal(0, tui.RunInteractive());
        var rendered = output.ToString();
        Assert.Contains("Atajos 1–4 de 20", rendered);
        Assert.Contains("Atajos 2–5 de 20", rendered);
        Assert.Contains("Atajos 1–3 de 3", rendered);
        Assert.Contains("run5", rendered);
        Assert.Contains("TUI finalizada", rendered);
        Assert.Equal(2, generation);
    }

    [Fact]
    public void TuiValueResolverRendersConfiguredHotkeyAndMissingValuesClearly()
    {
        var configuration = ShellConfiguration.CreateDefault();

        Assert.Equal("Win+Enter", TuiValueResolver.Resolve(configuration, "hotkeys.terminal", "config.toml"));
        Assert.Contains("ausente", TuiValueResolver.Resolve(configuration, "unknown.value", "config.toml"));
    }

    [Fact]
    public void WidgetOutputLimiterTruncatesByUtf8Bytes()
    {
        var result = WidgetOutputLimiter.Limit("áéí", 4);

        Assert.True(result.Truncated);
        Assert.Equal(4, Encoding.UTF8.GetByteCount(result.Output));
    }

    [Fact]
    public void TuiConfigurationLoadsStaticConfigurationAndCommandWidgets()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "tui.toml");
        try
        {
            File.WriteAllText(path, """
                [tui]
                enabled = true
                title = "Panel"

                [[tui.widgets]]
                id = "hello"
                title = "Saludo"
                type = "static"
                source = "Hola"

                [[tui.widgets]]
                id = "hotkey"
                title = "Terminal"
                type = "configuration"
                source = "hotkeys.terminal"

                [[tui.widgets]]
                id = "external"
                title = "Proceso"
                type = "command"
                command = "cmd.exe"
                arguments = ["/c", "echo", "ok"]
                timeout_ms = 1000
                max_output_bytes = 128
                """);

            var result = new TomlConfigurationProvider(new NullLogger()).Load(path);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(Path.GetFullPath(path), result.Configuration.ConfigurationPath);
            Assert.Equal(3, result.Configuration.Tui.Widgets.Count);
            Assert.Equal(TuiWidgetType.Command, result.Configuration.Tui.Widgets[2].Type);
            Assert.Equal(new[] { "/c", "echo", "ok" }, result.Configuration.Tui.Widgets[2].Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidTuiWidgetConfigurationReportsDuplicatesAndCommandErrors()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "invalid-tui.toml");
        try
        {
            File.WriteAllText(path, """
                [tui]
                title = ""

                [[tui.widgets]]
                id = "same"
                title = "Uno"
                type = "command"

                [[tui.widgets]]
                id = "same"
                title = "Dos"
                type = "unknown"
                """);

            var result = new TomlConfigurationProvider(new NullLogger()).Load(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("tui.title"));
            Assert.Contains(result.Errors, error => error.Contains("duplicado"));
            Assert.Contains(result.Errors, error => error.Contains("command"));
            Assert.Contains(result.Errors, error => error.Contains("tui.widgets.same.type"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TuiCanExitWithEscapeAndLeavesAReadableMessage()
    {
        using var input = new StringReader("\x1b");
        using var output = new StringWriter();
        var tui = new TuiApplication(
            ShellConfiguration.CreateDefault(),
            Array.Empty<string>(),
            "C:\\config.toml",
            new NullLogger(),
            input,
            output);

        Assert.Equal(0, tui.RunInteractive());
        Assert.Contains("TUI finalizada", output.ToString());
    }

    [Fact]
    public void TelemetryViewIsAvailableAndReportsMissingScriptWithoutEndingTui()
    {
        using var input = new StringReader("\x1b[B\x1b[B\x1b[B\r\x1b\x1b");
        using var output = new StringWriter();
        var tui = new TuiApplication(
            ShellConfiguration.CreateDefault(),
            Array.Empty<string>(),
            "C:\\config.toml",
            new NullLogger(),
            input,
            output,
            telemetryScriptPathResolver: () => null);

        Assert.Contains(tui.MainMenu.Items, item => item.Id == "telemetry");
        Assert.Equal(0, tui.RunInteractive());
        Assert.Contains("Telemetría de sesión", output.ToString());
        Assert.Contains("No se encontró scripts", output.ToString());
        Assert.Contains("TUI finalizada", output.ToString());
    }

    [Fact]
    public void MainMenuContainsLocalApiAndDisabledViewShowsGuidance()
    {
        using var input = new StringReader("\x1b");
        using var output = new StringWriter();
        var tui = new TuiApplication(ShellConfiguration.CreateDefault(), Array.Empty<string>(), "config.toml", new NullLogger(), input, output);

        Assert.Contains(tui.MainMenu.Items, item => item.Id == "api");
        tui.HandleKey(ConsoleKey.DownArrow);
        tui.HandleKey(ConsoleKey.Enter);
        Assert.Equal(0, tui.RunInteractive());
        Assert.Contains("API local de TenchyShell", output.ToString());
        Assert.Contains("API está deshabilitada", output.ToString());
        Assert.Contains("enabled = true", output.ToString());
    }

    [Fact]
    public async Task BenchmarkingWidgetRunnerWritesOnlySafeAggregateMetrics()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var recorder = new LiveBenchmarkRecorder(
                new BenchmarkConfiguration { Enabled = true, SampleIntervalSeconds = 3600, MaxStorageMb = 16 },
                new NullLogger(), Path.Combine(directory, "live"));
            recorder.Start();
            var runner = new BenchmarkingWidgetCommandRunner(
                new FixedWidgetRunner(WidgetExecutionResult.Success("secret-output")), recorder);

            var result = await runner.ExecuteAsync(new TuiWidgetConfiguration
            {
                Id = "secret-widget-id",
                Command = "C:\\secret-command.exe",
                Arguments = ["--token", "secret-value"]
            });

            Assert.True(result.Succeeded);
            var path = Assert.Single(Directory.GetFiles(recorder.DirectoryPath, "live-*.jsonl"));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var telemetry = await reader.ReadToEndAsync();
            Assert.Contains("periodic_execution_metrics", telemetry, StringComparison.Ordinal);
            Assert.Contains("\"executor\":\"tui\"", telemetry, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-output", telemetry, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-command", telemetry, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", telemetry, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-widget-id", telemetry, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BenchmarkingWidgetRunnerReportsAgeSinceLastValidOutput()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var recorder = new LiveBenchmarkRecorder(
                new BenchmarkConfiguration { Enabled = true, SampleIntervalSeconds = 3600, MaxStorageMb = 16 },
                new NullLogger(), Path.Combine(directory, "live"));
            recorder.Start();
            var times = new Queue<DateTimeOffset>([
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMilliseconds(1500)
            ]);
            var runner = new BenchmarkingWidgetCommandRunner(
                new SequenceWidgetRunner(WidgetExecutionResult.Success("ok"), WidgetExecutionResult.Failure("failed")),
                recorder,
                () => times.Dequeue());
            var widget = new TuiWidgetConfiguration { Id = "widget" };

            await runner.ExecuteAsync(widget);
            await runner.ExecuteAsync(widget);

            var path = Assert.Single(Directory.GetFiles(recorder.DirectoryPath, "live-*.jsonl"));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var telemetry = await reader.ReadToEndAsync();
            Assert.Contains("\"lastValidOutputAgeMilliseconds\":1500", telemetry, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TenchyShell.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class NullLogger : ILogger
    {
        public void Info(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }

    private sealed class FixedWidgetRunner(WidgetExecutionResult result) : IWidgetCommandRunner
    {
        public Task<WidgetExecutionResult> ExecuteAsync(TuiWidgetConfiguration widget, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class SequenceWidgetRunner(params WidgetExecutionResult[] results) : IWidgetCommandRunner
    {
        private readonly Queue<WidgetExecutionResult> results = new(results);

        public Task<WidgetExecutionResult> ExecuteAsync(TuiWidgetConfiguration widget, CancellationToken cancellationToken = default) =>
            Task.FromResult(results.Dequeue());
    }
}
