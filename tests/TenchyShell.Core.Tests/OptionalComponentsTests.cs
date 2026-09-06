using TenchyShell.Core.Configuration;
using TenchyShell.Core.Logging;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class OptionalComponentsTests
{
    [Fact]
    public void MissingOptionalsKeepBothComponentsEnabled()
    {
        var result = Load(string.Empty);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.True(result.Configuration.Optionals.Dock);
        Assert.True(result.Configuration.Optionals.ApplicationSearch);

        var selection = OptionalComponentsSelection.From(result.Configuration);
        Assert.True(selection.DockEnabled);
        Assert.True(selection.ApplicationSearchEnabled);
        Assert.Null(selection.DockDisabledReason);
        Assert.Null(selection.ApplicationSearchDisabledReason);
    }

    [Fact]
    public void OptionalsAcceptBothValuesAsTrue()
    {
        var result = Load("""
            [OPTIONALS]
            dock = true
            application_search = true
            """);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.True(result.Configuration.Optionals.Dock);
        Assert.True(result.Configuration.Optionals.ApplicationSearch);
    }

    [Fact]
    public void DockCanBeDisabledIndependently()
    {
        var configuration = Load("""
            [OPTIONALS]
            dock = false
            """).Configuration;

        var selection = OptionalComponentsSelection.From(configuration);

        Assert.False(selection.DockEnabled);
        Assert.True(selection.ApplicationSearchEnabled);
        Assert.Contains("OPTIONALS.dock = false", selection.DockDisabledReason);
    }

    [Fact]
    public void ApplicationSearchCanBeDisabledIndependently()
    {
        var configuration = Load("""
            [OPTIONALS]
            application_search = false
            """).Configuration;

        var selection = OptionalComponentsSelection.From(configuration);

        Assert.True(selection.DockEnabled);
        Assert.False(selection.ApplicationSearchEnabled);
        Assert.Contains("OPTIONALS.application_search = false", selection.ApplicationSearchDisabledReason);
    }

    [Fact]
    public void BothOptionalComponentsCanBeDisabled()
    {
        var configuration = Load("""
            [OPTIONALS]
            dock = false
            application_search = false
            """).Configuration;

        var selection = OptionalComponentsSelection.From(configuration);

        Assert.False(selection.DockEnabled);
        Assert.False(selection.ApplicationSearchEnabled);
        Assert.NotNull(selection.DockDisabledReason);
        Assert.NotNull(selection.ApplicationSearchDisabledReason);
    }

    [Theory]
    [InlineData("dock = \"false\"", "OPTIONALS.dock")]
    [InlineData("application_search = 1", "OPTIONALS.application_search")]
    public void NonBooleanOptionalValuesReturnReadableErrors(string value, string key)
    {
        var result = Load($"""
            [OPTIONALS]
            {value}
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains(key) && error.Contains("booleano"));
    }

    [Fact]
    public void StatusPanelDisabledAlsoDisablesEffectiveDock()
    {
        var configuration = Load("""
            [status_panel]
            enabled = false
            """).Configuration;

        var selection = OptionalComponentsSelection.From(configuration);

        Assert.False(selection.DockEnabled);
        Assert.True(selection.ApplicationSearchEnabled);
        Assert.Contains("status_panel.enabled = false", selection.DockDisabledReason);
    }

    [Fact]
    public void LauncherDisabledAlsoDisablesEffectiveApplicationSearch()
    {
        var configuration = Load("""
            [launcher]
            enabled = false
            """).Configuration;

        var selection = OptionalComponentsSelection.From(configuration);

        Assert.True(selection.DockEnabled);
        Assert.False(selection.ApplicationSearchEnabled);
        Assert.Contains("launcher.enabled = false", selection.ApplicationSearchDisabledReason);
    }

    [Fact]
    public void DisablingOneOptionalComponentLeavesTheOtherIndependent()
    {
        var configuration = new ShellConfiguration
        {
            Optionals = new OptionalComponentsConfiguration
            {
                Dock = false,
                ApplicationSearch = true
            }
        };

        var selection = OptionalComponentsSelection.From(configuration);

        Assert.False(selection.DockEnabled);
        Assert.True(selection.ApplicationSearchEnabled);
    }

    private static ConfigurationLoadResult Load(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TenchyShell.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.toml");

        try
        {
            File.WriteAllText(path, content);
            return new TomlConfigurationProvider(new FileLogger(directory)).Load(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
