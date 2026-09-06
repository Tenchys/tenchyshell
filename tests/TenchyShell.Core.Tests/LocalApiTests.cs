using System.Text.Json;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.LocalApi;
using TenchyShell.Core.Logging;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class LocalApiTests
{
    [Fact]
    public void DefaultsDisableTheLocalApi()
    {
        Assert.False(ShellConfiguration.CreateDefault().Api.Enabled);
        Assert.Equal("TenchyShell.LocalApi.v1", LocalApiDefaults.PipeName);
    }

    [Fact]
    public void CapabilityCatalogHasStableV1OrderAndGroups()
    {
        Assert.Equal(new[]
        {
            "workspace.active.get", "workspaces.get", "applications.get", "windows.get",
            "workspace.switch", "window.active.move", "subscribe"
        }, LocalApiCapabilityCatalog.Capabilities.Select(capability => capability.Name));
        Assert.Equal(new[] { "workspace.active.get", "workspaces.get", "applications.get", "windows.get" }, LocalApiCapabilityCatalog.Queries);
        Assert.Equal(new[] { "workspace.switch", "window.active.move" }, LocalApiCapabilityCatalog.Commands);
        Assert.Equal(new[] { "workspace", "windows", "applications" }, LocalApiCapabilityCatalog.Topics);
        Assert.Equal(1, LocalApiDefaults.Version);
        Assert.Equal(64 * 1024, LocalApiDefaults.MaxMessageBytes);
        Assert.Equal(5000, LocalApiDefaults.OperationTimeoutMilliseconds);
    }

    [Fact]
    public void SnapshotSerializesStableCamelCaseFields()
    {
        var snapshot = new LocalApiSnapshot(2,
            new[] { new LocalApiWorkspace(1, 0), new LocalApiWorkspace(2, 1) },
            new[] { new LocalApiWindow("0x2A", 2, "Terminal", true, false, true) },
            new[] { new LocalApiApplication("Terminal", "Executable", null) });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Assert.Equal(2, document.RootElement.GetProperty("activeWorkspace").GetInt32());
        Assert.Equal("0x2A", document.RootElement.GetProperty("windows")[0].GetProperty("hwnd").GetString());
        Assert.Equal("Terminal", document.RootElement.GetProperty("applications")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void LocalApiConfigurationLoadsEnabledValue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TenchyShell.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "api.toml");
        try
        {
            File.WriteAllText(path, "[api]\nenabled = true\n");
            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);
            Assert.True(result.IsValid);
            Assert.True(result.Configuration.Api.Enabled);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LocalApiConfigurationRejectsNonBooleanEnabledValue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TenchyShell.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "api.toml");
        try
        {
            File.WriteAllText(path, "[api]\nenabled = \"yes\"\n");
            var result = new TomlConfigurationProvider(new FileLogger(directory)).Load(path);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("api.enabled", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
