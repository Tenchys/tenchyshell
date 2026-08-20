using Xunit;
using TenchyShell.Core.Applications;
using TenchyShell.Core.Commands;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Network;
using TenchyShell.Core.Layout;
using TenchyShell.Core.Processes;
using TenchyShell.Core.Session;
using TenchyShell.Core.Runtime;
using TenchyShell.Core.StatusPanel;
using TenchyShell.Core.Windows;
using TenchyShell.Core.SystemTray;
using TenchyShell.Core.Wallpaper;
using TenchyShell.Core.InputLanguages;

namespace TenchyShell.Core.Tests;

public sealed class PlaceholderTests
{
    [Fact]
    public void LegacyMigrationCopiesKnownFilesAndIsIdempotent()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "MinimalShell");
        var target = Path.Combine(directory, "TenchyShell");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "state"));
            File.WriteAllText(Path.Combine(source, "MinimalShell.toml"), "[terminal]");
            File.WriteAllText(Path.Combine(source, "state", "wallpaper.json"), "{\"LastWallpaperPath\":\"C:\\\\wallpaper.png\"}");

            var first = LegacyDataMigrator.Migrate(source, target);
            var second = LegacyDataMigrator.Migrate(source, target);

            Assert.Contains(first.Items, item => item.Status == MigrationItemStatus.Copied && item.TargetRelativePath == "TenchyShell.toml");
            Assert.Contains(first.Items, item => item.Status == MigrationItemStatus.Copied && item.TargetRelativePath.EndsWith("wallpaper.json"));
            Assert.Contains(second.Items, item => item.Status == MigrationItemStatus.AlreadyPresent && item.TargetRelativePath == "TenchyShell.toml");
            Assert.True(File.Exists(Path.Combine(source, "MinimalShell.toml")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyMigrationRejectsInvalidStateAndDoesNotOverwriteConflicts()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "MinimalShell");
        var target = Path.Combine(directory, "TenchyShell");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "state"));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "MinimalShell.toml"), "source");
            File.WriteAllText(Path.Combine(target, "TenchyShell.toml"), "target");
            File.WriteAllText(Path.Combine(source, "state", "wallpaper.json"), "{ invalid }");

            var result = LegacyDataMigrator.Migrate(source, target);

            Assert.True(result.HasErrors);
            Assert.Contains(result.Items, item => item.Status == MigrationItemStatus.Conflict);
            Assert.Contains(result.Items, item => item.Status == MigrationItemStatus.Invalid);
            Assert.Equal("target", File.ReadAllText(Path.Combine(target, "TenchyShell.toml")));
            Assert.False(File.Exists(Path.Combine(target, "state", "wallpaper.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NetworkSnapshotCarriesInterfaceKindsAndConnectionState()
    {
        var snapshot = new NetworkSnapshot(
            true,
            true,
            new[]
            {
                new NetworkInterfaceSnapshot("wifi", "Wi-Fi", NetworkInterfaceKind.Wifi, "Up", 1000, true),
                new NetworkInterfaceSnapshot("ethernet", "Ethernet", NetworkInterfaceKind.Ethernet, "Down", 0, false)
            },
            null);

        Assert.True(snapshot.HasAnyConnection);
        Assert.Equal(NetworkInterfaceKind.Wifi, snapshot.Interfaces[0].Kind);
        Assert.False(snapshot.Interfaces[1].IsOperational);
    }

    [Fact]
    public void WallpaperCatalogKeepsSortedEntriesAndErrorsSeparate()
    {
        var catalog = new WallpaperCatalog(
            new[]
            {
                new WallpaperEntry("b.png", "b.png"),
                new WallpaperEntry("a.jpg", "a.jpg")
            },
            null);

        Assert.Equal(2, catalog.Items.Count);
        Assert.Null(catalog.Error);
        Assert.Equal("a.jpg", catalog.Items.OrderBy(item => item.Name).First().Name);
    }

    [Fact]
    public void WallpaperStateStoreReturnsNoPathWhenStateDoesNotExist()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new WallpaperStateStore(Path.Combine(directory, "state", "wallpaper.json"));

            var result = store.Load();

            Assert.False(result.HasSavedWallpaper);
            Assert.Null(result.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WallpaperStateStoreReportsInvalidJson()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var statePath = Path.Combine(directory, "state", "wallpaper.json");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, "{ no es json }");
            var store = new WallpaperStateStore(statePath);

            var result = store.Load();

            Assert.False(result.HasSavedWallpaper);
            Assert.NotNull(result.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WallpaperStateStoreSavesAbsolutePathAndReplacesPreviousSelection()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var statePath = Path.Combine(directory, "state", "wallpaper.json");
            var store = new WallpaperStateStore(statePath);
            var first = Path.Combine(directory, "first.png");
            var second = Path.Combine(directory, "second.png");

            Assert.True(store.Save(first).Succeeded);
            Assert.True(store.Save(second).Succeeded);

            var result = store.Load();
            Assert.True(result.HasSavedWallpaper);
            Assert.Equal(Path.GetFullPath(second), result.LastWallpaperPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NetworkSnapshotCarriesEthernetDetailsAndWifiNetworks()
    {
        var ethernet = new NetworkInterfaceSnapshot("ethernet", "Ethernet", NetworkInterfaceKind.Ethernet, "Up", 1_000_000_000, true)
        {
            IpAddresses = new[] { "192.168.1.20", "fe80::1" }
        };
        var snapshot = new NetworkSnapshot(true, true, new[] { ethernet }, null)
        {
            WifiNetworks = new[] { new WifiNetworkSnapshot("Casa", 87, true, false) }
        };

        Assert.Equal("192.168.1.20", snapshot.Interfaces[0].IpAddresses[0]);
        Assert.Equal(1_000_000_000, snapshot.Interfaces[0].SpeedBitsPerSecond);
        Assert.Equal("Casa", snapshot.WifiNetworks[0].Ssid);
        Assert.Equal(87, snapshot.WifiNetworks[0].SignalQuality);
        Assert.True(snapshot.WifiNetworks[0].IsSecured);
    }
    [Fact]
    public void SystemTrayStateRemovesDuplicateIdsAndNavigates()
    {
        var state = new SystemTrayState(new[]
        {
            new SystemTrayItem("shell", "TenchyShell", "Activo"),
            new SystemTrayItem("shell", "Duplicado", "Ignorado"),
            new SystemTrayItem("network", "Red", "Disponible")
        });

        Assert.Equal(2, state.Items.Count);
        Assert.Equal("shell", state.SelectedItem?.Id);
        state.Move();
        Assert.Equal("network", state.SelectedItem?.Id);
        state.Move();
        Assert.Equal("shell", state.SelectedItem?.Id);
    }

    [Fact]
    public void SystemTrayStateCanSelectTheActiveInputLanguageInitially()
    {
        var state = new SystemTrayState(new[]
        {
            new SystemTrayItem("input-language:409", "English", "Seleccionar"),
            new SystemTrayItem("input-language:C0A", "Español", "Activo")
        }, "input-language:C0A");

        Assert.Equal("input-language:C0A", state.SelectedItem?.Id);
        state.Move(backwards: true);
        Assert.Equal("input-language:409", state.SelectedItem?.Id);
    }

    [Theory]
    [InlineData("ES", "Español (Chile)", "short", "ES")]
    [InlineData("ES", "Español (Chile)", "full", "Español (Chile)")]
    [InlineData("EN", "English", "unexpected", "EN")]
    public void InputLanguageLabelUsesConfiguredFormat(string shortName, string displayName, string format, string expected)
    {
        Assert.Equal(expected, InputLanguageLabel.Format(shortName, displayName, format));
    }
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
            Assert.True(result.Configuration.SystemTray.Enabled);
            Assert.Equal("Ctrl+Alt+T", result.Configuration.SystemTray.Hotkey);
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

                [status_panel]
                hotkey = "Ctrl+Alt+P"
                width = 240
                height = 100
                edge_zone = 6

                [system_tray]
                enabled = false
                hotkey = "Ctrl+Alt+Y"
                width = 500
                height = 300

                [[system_tray.items]]
                id = "mouse"
                title = "Mouse"
                command = "powershell.exe"
                arguments = ["-File", "mouse.ps1"]
                interval_ms = 5000
                timeout_ms = 1500

                [system_tray.actions.mouse.open]
                command = "powershell.exe"
                arguments = ["-Command", "Write-Host mouse"]

                [input_language]
                enabled = true
                title = "Teclado"
                label_format = "full"
                hotkey = "Ctrl+Alt+L"
                """);

            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);

            Assert.True(result.IsValid);
            Assert.False(result.UsedDefaults);
            Assert.Equal("custom-terminal.exe", result.Configuration.Terminal.Command);
            Assert.Equal("firefox.exe", result.Configuration.Applications.Browser);
            Assert.Equal("Ctrl+Alt+T", result.Configuration.Hotkeys.Terminal);
            Assert.Equal("yazi.exe", result.Configuration.FileManager.Command);
            Assert.Equal("Ctrl+Alt+1", result.Configuration.WorkspaceHotkeys.Switch[0]);
            Assert.Equal("Ctrl+Alt+Shift+9", result.Configuration.WorkspaceHotkeys.Move[8]);
            Assert.Equal("Ctrl+Alt+P", result.Configuration.StatusPanel.Hotkey);
            Assert.Equal(240, result.Configuration.StatusPanel.Width);
            Assert.Equal(100, result.Configuration.StatusPanel.Height);
            Assert.Equal(6, result.Configuration.StatusPanel.EdgeZone);
            Assert.False(result.Configuration.SystemTray.Enabled);
            Assert.Equal("Ctrl+Alt+Y", result.Configuration.SystemTray.Hotkey);
            Assert.Equal(500, result.Configuration.SystemTray.Width);
            Assert.Single(result.Configuration.SystemTray.Items);
            Assert.Equal("mouse", result.Configuration.SystemTray.Items[0].Id);
            Assert.Equal("powershell.exe", result.Configuration.SystemTray.Items[0].Command);
            Assert.Equal("powershell.exe", result.Configuration.SystemTray.Actions["mouse.open"].Command);
            Assert.True(result.Configuration.InputLanguage.Enabled);
            Assert.Equal("Teclado", result.Configuration.InputLanguage.Title);
            Assert.Equal("full", result.Configuration.InputLanguage.LabelFormat);
            Assert.Equal("Ctrl+Alt+L", result.Configuration.InputLanguage.Hotkey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidInputLanguageConfigurationReturnsReadableErrors()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "invalid-input-language.toml");
        try
        {
            File.WriteAllText(path, """
                [input_language]
                enabled = true
                title = ""
                label_format = "icon"
                """);

            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("input_language.title"));
            Assert.Contains(result.Errors, error => error.Contains("input_language.label_format"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DefaultLayoutUsesOneRowAndTwoColumns()
    {
        var catalog = new LayoutZoneCatalog();
        var zones = catalog.GetZonesForMonitor("\\.\\DISPLAY1", isPrimary: true);

        Assert.Equal(2, zones.Count);
        Assert.Equal(new LayoutZone(1, "*", 0, 0, 0.5, 1), zones[0]);
        Assert.Equal(new LayoutZone(2, "*", 0.5, 0, 1, 1), zones[1]);
    }

    [Fact]
    public void LayoutCatalogPrefersExactMonitorThenPrimaryThenWildcard()
    {
        var zones = new[]
        {
            new LayoutZone(1, "*", 0, 0, 1, 1),
            new LayoutZone(1, "primary", 0, 0, 0.5, 1),
            new LayoutZone(1, "\\.\\DISPLAY2", 0.5, 0, 1, 1)
        };
        var catalog = new LayoutZoneCatalog(zones);

        Assert.Equal(0.5, catalog.GetZonesForMonitor("\\.\\DISPLAY2", false)[0].Left);
        Assert.Equal(0, catalog.GetZonesForMonitor("\\.\\DISPLAY1", true)[0].Left);
        Assert.Equal(0, catalog.GetZonesForMonitor("\\.\\DISPLAY3", false)[0].Left);
    }

    [Fact]
    public void LayoutZoneCalculatorMapsNormalizedCoordinatesToWorkArea()
    {
        var zone = new LayoutZone(1, "*", 0.5, 0, 1, 1);

        var result = LayoutZoneCalculator.ToWindowRect(zone, new WindowRect(-100, 40, 900, 1040));

        Assert.Equal(new WindowRect(400, 40, 900, 1040), result);
    }

    [Fact]
    public void LayoutValidatorAllowsTouchingEdgesAndRejectsOverlapAndDuplicates()
    {
        var valid = LayoutZoneValidator.Validate(new[]
        {
            new LayoutZone(1, "*", 0, 0, 0.5, 1),
            new LayoutZone(2, "*", 0.5, 0, 1, 1)
        });
        var invalid = LayoutZoneValidator.Validate(new[]
        {
            new LayoutZone(1, "*", 0, 0, 0.75, 1),
            new LayoutZone(1, "*", 0.5, 0, 1, 1),
            new LayoutZone(3, "*", 0, 0, 0.5, 0.5)
        });

        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Errors));
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Contains("repetida", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(invalid.Errors, error => error.Contains("superponen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidTomlLoadsLayoutZonesAndHotkeys()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "layout.toml");

        try
        {
            File.WriteAllText(path, """
                [layout]
                enabled = true
                max_zones = 9
                default_preset = "1x2"
                zone_number_size_percent = 6.5

                [[layout.zones]]
                monitor = "*"
                number = 1
                left = 0.0
                top = 0.0
                right = 0.5
                bottom = 1.0

                [[layout.zones]]
                monitor = "*"
                number = 2
                left = 0.5
                top = 0.0
                right = 1.0
                bottom = 1.0

                [hotkeys.layout]
                zone_1 = "Ctrl+Win+1"
                drag_modifier = "Ctrl+Shift"
                """);

            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal("1x2", result.Configuration.Layout.DefaultPreset);
            Assert.Equal(6.5, result.Configuration.Layout.ZoneNumberSizePercent);
            Assert.Equal(2, result.Configuration.Layout.Zones.Count);
            Assert.Equal("Ctrl+Win+1", result.Configuration.LayoutHotkeys.Zones[0]);
            Assert.Equal("Ctrl+Shift", result.Configuration.LayoutHotkeys.DragModifier);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidLayoutTomlReturnsReadableErrors()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "invalid-layout.toml");

        try
        {
            File.WriteAllText(path, """
                [layout]
                enabled = true
                max_zones = 10
                default_preset = "2x2"
                zone_number_size_percent = 0

                [[layout.zones]]
                monitor = "*"
                number = 1
                left = 0.0
                top = 0.0
                right = 1.2
                bottom = 1.0
                """);

            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("max_zones"));
            Assert.Contains(result.Errors, error => error.Contains("default_preset"));
            Assert.Contains(result.Errors, error => error.Contains("zone_number_size_percent"));
            Assert.Contains(result.Errors, error => error.Contains("geometría"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LayoutDragStateMachineRequiresAWindowAndZoneToComplete()
    {
        var state = new LayoutDragStateMachine();

        Assert.False(state.Begin(IntPtr.Zero));
        Assert.True(state.Begin((IntPtr)123));
        Assert.False(state.TryComplete(out _, out _));
        Assert.True(state.SetHoveredZone(2));
        Assert.True(state.TryComplete(out var window, out var zone));
        Assert.Equal((IntPtr)123, window);
        Assert.Equal(2, zone);
        Assert.False(state.IsDragging);
    }

    [Fact]
    public void LayoutDragStateMachineCancelsWithoutChangingTheWindow()
    {
        var state = new LayoutDragStateMachine();

        Assert.True(state.Begin((IntPtr)456));
        Assert.True(state.SetHoveredZone(3));
        state.Cancel();

        Assert.False(state.IsDragging);
        Assert.Null(state.HoveredZone);
        Assert.False(state.TryComplete(out _, out _));
    }

    [Fact]
    public void StatusPanelUsesWorkspaceOneAndFormatsLocalTime()
    {
        var state = new StatusPanelState();

        Assert.Equal("Workspace 1", state.WorkspaceLabel);
        Assert.Equal("14:32:08", state.GetTimeLabel(new DateTime(2026, 8, 18, 14, 32, 8)));

        state.SetWorkspace(4);

        Assert.Equal("Workspace 4", state.WorkspaceLabel);
    }

    [Fact]
    public void StatusPanelEdgeRevealHidesWhenPointerLeaves()
    {
        var state = new StatusPanelVisibilityState();

        state.ShowFromEdge();

        Assert.True(state.IsVisible);
        Assert.False(state.IsPinnedByHotkey);
        Assert.False(state.HideWhenPointerLeaves(pointerInsidePanel: true));
        Assert.True(state.HideWhenPointerLeaves(pointerInsidePanel: false));
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void StatusPanelHotkeyKeepsPanelVisibleUntilToggledAgain()
    {
        var state = new StatusPanelVisibilityState();

        Assert.True(state.ToggleByHotkey());
        Assert.True(state.IsVisible);
        Assert.True(state.IsPinnedByHotkey);
        Assert.False(state.HideWhenPointerLeaves(pointerInsidePanel: false));
        Assert.False(state.ToggleByHotkey());
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void StatusPanelEdgeDetectorAcceptsOnlyTheConfiguredPrimaryEdgeZone()
    {
        var workArea = new StatusPanelRectangle(0, 0, 1920, 1080);

        Assert.True(StatusPanelEdgeDetector.IsAtLeftEdge(new StatusPanelPoint(4, 500), workArea, edgeZone: 4));
        Assert.False(StatusPanelEdgeDetector.IsAtLeftEdge(new StatusPanelPoint(5, 500), workArea, edgeZone: 4));
        Assert.False(StatusPanelEdgeDetector.IsAtLeftEdge(new StatusPanelPoint(4, 1080), workArea, edgeZone: 4));
        Assert.True(StatusPanelEdgeDetector.IsInside(new StatusPanelPoint(10, 20), new StatusPanelRectangle(0, 0, 220, 96)));
        Assert.False(StatusPanelEdgeDetector.IsInside(new StatusPanelPoint(220, 20), new StatusPanelRectangle(0, 0, 220, 96)));
    }

    [Fact]
    public void InvalidStatusPanelConfigurationReturnsReadableErrors()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "invalid-status-panel.toml");

        try
        {
            File.WriteAllText(path, """
                [status_panel]
                width = 0
                height = -1
                edge_zone = -2
                monitor = "secondary"
                """);

            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("status_panel.width"));
            Assert.Contains(result.Errors, error => error.Contains("status_panel.height"));
            Assert.Contains(result.Errors, error => error.Contains("status_panel.edge_zone"));
            Assert.Contains(result.Errors, error => error.Contains("status_panel.monitor"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StartupDiagnosticsReportsMissingDependenciesWithoutLaunchingThem()
    {
        var checker = new RecordingAvailabilityChecker(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["wezterm.exe"] = true,
            ["powershell.exe"] = true,
            ["yazi.exe"] = false,
            ["brave.exe"] = true
        });
        var configuration = new ShellConfiguration
        {
            Terminal = new TerminalConfiguration
            {
                Command = "wezterm.exe",
                CommandShell = "powershell.exe"
            },
            FileManager = new FileManagerConfiguration { Command = "yazi.exe" },
            Applications = new ApplicationConfiguration { Browser = "brave.exe" }
        };

        var result = StartupDiagnostics.Run(configuration, checker);

        Assert.True(result.HasMissingDependencies);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Component == "file manager" && !diagnostic.IsAvailable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Component == "layout" && diagnostic.IsAvailable);
        Assert.Equal(4, checker.CheckedCommands.Count);
    }

    [Fact]
    public void DisabledStatusPanelDoesNotRequireAHotkey()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "disabled-panel.toml");

        try
        {
            File.WriteAllText(path, """
                [status_panel]
                enabled = false
                hotkey = ""
                """);

            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);

            Assert.True(result.IsValid);
            Assert.False(result.Configuration.StatusPanel.Enabled);
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
        var name = $"TenchyShell.Tests.{Guid.NewGuid():N}";

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
        var directory = Path.Combine(Path.GetTempPath(), "TenchyShell.Tests", Guid.NewGuid().ToString("N"));
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

    private sealed class RecordingAvailabilityChecker : ICommandAvailabilityChecker
    {
        private readonly IReadOnlyDictionary<string, bool> availability;

        public RecordingAvailabilityChecker(IReadOnlyDictionary<string, bool> availability)
        {
            this.availability = availability;
        }

        public List<string> CheckedCommands { get; } = new();

        public bool IsAvailable(string command)
        {
            CheckedCommands.Add(command);
            return availability.TryGetValue(command, out var available) && available;
        }
    }
}
