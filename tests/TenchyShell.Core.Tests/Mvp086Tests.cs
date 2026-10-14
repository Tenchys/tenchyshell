using TenchyShell.Core.Configuration;
using TenchyShell.Core.Session;
using TenchyShell.Core.StatusPanel;
using TenchyShell.Core.Yazi;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class Mvp086Tests
{
    [Fact]
    public void DockNavigationFiltersHiddenItemsAndAlwaysKeepsRecoveryAvailable()
    {
        var state = DockNavigationState.FromConfiguration(new DockConfiguration
        {
            Categories = new[] { new DockCategoryConfiguration
            {
                Id = "aplicaciones", Title = "Aplicaciones", Order = 20,
                Items = new[] { new DockItemConfiguration { Id = "terminal", Title = "Terminal", Action = "terminal" },
                    new DockItemConfiguration { Id = "files", Title = "Oculto", Action = "files", Visible = false } }
            } }
        });

        Assert.Equal(new[] { "aplicaciones", "sesion" }, state.Categories.Select(c => c.Id));
        Assert.Single(state.Categories[0].Items);
        Assert.Contains(state.Categories.Single(c => c.Id == "sesion").Items, i => i.Action == "recovery");
        state.ToggleExpanded();
        Assert.False(state.Expanded);
    }

    [Fact]
    public void DockLayoutReservesConfirmationSpaceAndUsesLargerRows()
    {
        Assert.Equal(22, DockLayoutMetrics.RowHeight);
        Assert.Equal(95, DockLayoutMetrics.FirstRowTop);
        Assert.Equal(22, (int)Math.Round(DockLayoutMetrics.BaseRowHeight * 1.15, MidpointRounding.AwayFromZero));

        var categories = new[]
        {
            new DockNavigationCategory("sesion", "Sesión", true, new[]
            {
                new DockNavigationItem("shutdown", "Apagar", "shutdown", true)
            })
        };

        Assert.Equal(147, DockLayoutMetrics.GetDockHeight(0, categories));
        Assert.True(DockLayoutMetrics.TryGetRow(DockLayoutMetrics.FirstRowTop, out var firstRow));
        Assert.Equal(0, firstRow);
        Assert.True(DockLayoutMetrics.TryGetRow(DockLayoutMetrics.FirstRowTop + DockLayoutMetrics.RowHeight, out var secondRow));
        Assert.Equal(1, secondRow);
        Assert.False(DockLayoutMetrics.TryGetRow(DockLayoutMetrics.FirstRowTop - 1, out _));
    }

    [Fact]
    public void DockNavigationHitTestingMatchesRenderedRowsAfterConfirmationSpace()
    {
        var state = DockNavigationState.FromConfiguration(new DockConfiguration
        {
            Categories = new[]
            {
                new DockCategoryConfiguration
                {
                    Id = "sesion", Title = "Sesión", Order = 30,
                    Items = new[]
                    {
                        new DockItemConfiguration { Id = "shutdown", Title = "Apagar", Action = "shutdown" }
                    }
                }
            }
        });

        Assert.True(state.SelectAt(DockLayoutMetrics.FirstRowTop + DockLayoutMetrics.RowHeight));
        Assert.Equal("shutdown", state.SelectedItem!.Action);
    }

    [Fact]
    public async Task SessionConfirmationRequiresSecondMatchingRequestAndRejectsDuplicates()
    {
        var state = new SessionConfirmationStateMachine();
        var executions = 0;
        Assert.True(state.Request(SessionAction.Shutdown));
        Assert.False(state.Request(SessionAction.Restart));
        Assert.Equal(SessionConfirmationStatus.Idle, state.Status);
        Assert.True(state.Request(SessionAction.Shutdown));
        Assert.False(await state.ConfirmAsync(_ => { executions++; return Task.FromResult(false); }));
        Assert.Equal(1, executions);
        Assert.False(await state.ConfirmAsync(_ => { executions++; return Task.FromResult(true); }));
        Assert.Equal(1, executions);
    }

    [Fact]
    public void YaziResolverPrefersEnvironmentPathAndReaderIsReadOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenchyshell-yazi-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        var file = Path.Combine(config, "keymap.toml");
        File.WriteAllText(file, "[manager]\nprepend_keymap = [{ on = [\"g\", \"g\"], run = \"arrow top\", desc = \"Top\" }]\n");
        var before = File.GetLastWriteTimeUtc(file);
        var result = new YaziShortcutReader().Read(config, Path.Combine(root, "other"));
        Assert.Empty(result.Errors);
        Assert.Equal(YaziShortcutLoadStatus.Loaded, result.Status);
        Assert.Equal("g g", result.Shortcuts.Single().Sequence);
        Assert.Equal("arrow top", result.Shortcuts.Single().Action);
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void YaziReaderUsesBundledPresetWhenUserFileIsAbsent()
    {
        var result = new YaziShortcutReader().Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal(YaziShortcutLoadStatus.DefaultPreset, result.Status);
        Assert.Equal(YaziShortcutSource.DefaultPreset, result.Source);
        Assert.Contains(result.Shortcuts, shortcut => shortcut.Context == "mgr" && shortcut.Sequence == "q");
    }
}
