using Xunit;
using MinimalShell.Core.Applications;
using MinimalShell.Core.Commands;
using MinimalShell.Core.Configuration;
using MinimalShell.Core.Logging;
using MinimalShell.Core.Processes;
using MinimalShell.Core.Session;
using MinimalShell.Core.Runtime;

namespace MinimalShell.Core.Tests;

public sealed class PlaceholderTests
{
    [Fact]
    public void MissingPathUsesValidDefaultConfiguration()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var logger = new FileLogger(directory);
            var result = new TomlConfigurationProvider(logger).Load();

            Assert.True(result.IsValid);
            Assert.True(result.UsedDefaults);
            Assert.Equal("wezterm-gui.exe", result.Configuration.Terminal.Command);
            Assert.Equal("yazi.exe", result.Configuration.FileManager.Command);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidTomlLoadsConfiguredValues()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "config.toml");

        try
        {
            File.WriteAllText(path, """
                [terminal]
                command = "custom-terminal.exe"

                [applications]
                browser = "firefox.exe"

                [hotkeys]
                terminal = "Ctrl+Alt+T"
                """);

            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);

            Assert.True(result.IsValid);
            Assert.False(result.UsedDefaults);
            Assert.Equal("custom-terminal.exe", result.Configuration.Terminal.Command);
            Assert.Equal("firefox.exe", result.Configuration.Applications.Browser);
            Assert.Equal("Ctrl+Alt+T", result.Configuration.Hotkeys.Terminal);
            Assert.Equal("yazi.exe", result.Configuration.FileManager.Command);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidConfigurationReturnsErrorsAndWritesLog()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "invalid.toml");

        try
        {
            File.WriteAllText(path, """
                [terminal]
                command = ""
                """);

            var logger = new FileLogger(directory);
            var result = new TomlConfigurationProvider(logger).Load(path);

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
            Assert.Equal("wezterm-gui.exe", result.Configuration.Terminal.Command);
            Assert.Contains("Configuración inválida", File.ReadAllText(logger.LogFilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileLoggerCreatesLogFileAndWritesMessage()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var logger = new FileLogger(directory);
            logger.Info("mensaje de prueba");

            Assert.True(File.Exists(logger.LogFilePath));
            Assert.Contains("mensaje de prueba", File.ReadAllText(logger.LogFilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LaunchFilesUsesConfiguredTerminalAndYazi()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var launcher = new RecordingProcessLauncher();
            var actions = new ShellActions(
                ShellConfiguration.CreateDefault(),
                launcher,
                new FileLogger(directory));

            var result = actions.LaunchFiles();

            Assert.True(result.Succeeded);
            Assert.NotNull(launcher.LastRequest);
            Assert.Equal("wezterm-gui.exe", launcher.LastRequest!.FileName);
            Assert.Equal(new[] { "start", "--always-new-process", "--", "yazi.exe" }, launcher.LastRequest.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LaunchBrowserUsesConfiguredApplication()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var launcher = new RecordingProcessLauncher();
            var configuration = new ShellConfiguration
            {
                Applications = new ApplicationConfiguration { Browser = "firefox.exe" }
            };
            var actions = new ShellActions(configuration, launcher, new FileLogger(directory));

            var result = actions.LaunchBrowser();

            Assert.True(result.Succeeded);
            Assert.Equal("firefox.exe", launcher.LastRequest!.FileName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ApplicationCatalogFiltersRanksAndDeduplicatesApplications()
    {
        var applications = new[]
        {
            new ApplicationEntry("Visual Studio Code", ApplicationActivationKind.Executable, "code.exe"),
            new ApplicationEntry("Visual Studio", ApplicationActivationKind.Executable, "devenv.exe"),
            new ApplicationEntry("visual studio code", ApplicationActivationKind.Executable, "code.exe"),
            new ApplicationEntry("", ApplicationActivationKind.Executable, "invalid.exe")
        };
        var catalog = new ApplicationSearchCatalog(applications);

        var results = catalog.Search("visual");

        Assert.Equal(2, catalog.GetAll().Count);
        Assert.Equal("Visual Studio", results[0].DisplayName);
        Assert.Equal("Visual Studio Code", results[1].DisplayName);
    }

    [Fact]
    public void ShellActionsBuildsInteractiveTerminalCommand()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var launcher = new RecordingProcessLauncher();
            var configuration = new ShellConfiguration
            {
                Terminal = new TerminalConfiguration
                {
                    Command = "wezterm.exe",
                    CommandArguments = "start -- powershell.exe -NoExit -Command"
                }
            };
            var actions = new ShellActions(configuration, launcher, new FileLogger(directory));

            var result = actions.LaunchCommand("git status");

            Assert.True(result.Succeeded);
            Assert.Equal("wezterm.exe", launcher.LastRequest!.FileName);
            Assert.Equal(
                new[] { "start", "--", "powershell.exe", "-NoExit", "-Command", "git status" },
                launcher.LastRequest.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProcessLaunchResultAllowsSuccessfulShellActivationWithoutProcessId()
    {
        var result = ProcessLaunchResult.Success(null);

        Assert.True(result.Succeeded);
        Assert.Null(result.ProcessId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SessionActionRequiresExplicitConfirmation()
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var launcher = new RecordingProcessLauncher();
            var service = new SessionActionService(launcher, new FileLogger(directory));

            var result = service.Execute(SessionAction.Shutdown, confirmed: false);

            Assert.False(result.Succeeded);
            Assert.Null(launcher.LastRequest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(SessionAction.Logout, new[] { "/l" })]
    [InlineData(SessionAction.Shutdown, new[] { "/s", "/t", "0" })]
    [InlineData(SessionAction.Restart, new[] { "/r", "/t", "0" })]
    public void ConfirmedSessionActionBuildsExpectedShutdownCommand(SessionAction action, string[] expectedArguments)
    {
        var directory = CreateTemporaryDirectory();

        try
        {
            var launcher = new RecordingProcessLauncher();
            var service = new SessionActionService(launcher, new FileLogger(directory));

            var result = service.Execute(action, confirmed: true);

            Assert.True(result.Succeeded);
            Assert.Equal("shutdown.exe", launcher.LastRequest!.FileName);
            Assert.Equal(expectedArguments, launcher.LastRequest.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SingleInstanceGuardRejectsASecondInstanceWithTheSameName()
    {
        var name = $"MinimalShell.Tests.{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));

        try
        {
            Assert.False(SingleInstanceGuard.TryAcquire(name, out var second));
            Assert.Null(second);
        }
        finally
        {
            first!.Dispose();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MinimalShell.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public ProcessLaunchRequest? LastRequest { get; private set; }

        public ProcessLaunchResult Launch(ProcessLaunchRequest request)
        {
            LastRequest = request;
            return ProcessLaunchResult.Success(1234);
        }
    }
}
