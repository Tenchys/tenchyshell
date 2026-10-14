using TenchyShell.Core.Startup;
using TenchyShell.Core;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class StartupProgressTests
{
    [Fact]
    public void ProductVersionIsCentralized()
    {
        Assert.Equal("TenchyShell", ProductInfo.Name);
        Assert.Equal("0.8.23", ProductInfo.Version);
    }

    [Fact]
    public void ModulesMustBeReportedInOrder()
    {
        var progress = new StartupProgressState();

        progress.ReportStarted(StartupModule.MigrationAndConfiguration, "migración");
        progress.ReportCompleted(StartupModule.MigrationAndConfiguration);

        Assert.Throws<InvalidOperationException>(() =>
            progress.ReportStarted(StartupModule.WorkspacesAndWindowTracking, "workspaces"));
    }

    [Fact]
    public void CurrentModuleAndMessageAreExposed()
    {
        var progress = new StartupProgressState();

        progress.ReportStarted(StartupModule.MigrationAndConfiguration, "validando configuración");

        Assert.Equal(StartupModule.MigrationAndConfiguration, progress.Snapshot.CurrentModule);
        Assert.Equal("validando configuración", progress.Snapshot.Message);
        Assert.Equal(StartupProgressStatus.InProgress, progress.Snapshot.Status);
    }

    [Fact]
    public void CompletingEveryModuleMakesTheSessionReady()
    {
        var progress = new StartupProgressState();

        foreach (var module in Enum.GetValues<StartupModule>())
        {
            progress.ReportStarted(module, module.ToString());
            progress.ReportCompleted(module);
        }

        Assert.Equal(StartupProgressStatus.Ready, progress.Snapshot.Status);
        Assert.True(progress.Snapshot.IsReady);
        Assert.Equal(Enum.GetValues<StartupModule>(), progress.Snapshot.CompletedModules);
    }

    [Fact]
    public void ErrorKeepsTheFailedModuleAndMessage()
    {
        var progress = new StartupProgressState();
        var exception = new InvalidOperationException("configuración inválida");

        progress.ReportStarted(StartupModule.MigrationAndConfiguration, "migración");
        progress.ReportCompleted(StartupModule.MigrationAndConfiguration);
        progress.ReportStarted(StartupModule.CoreServices, "servicios");
        progress.ReportError(StartupModule.CoreServices, "Falló CoreServices", exception);

        Assert.Equal(StartupProgressStatus.Failed, progress.Snapshot.Status);
        Assert.Equal(StartupModule.CoreServices, progress.Snapshot.CurrentModule);
        Assert.Equal("Falló CoreServices", progress.Snapshot.ErrorMessage);
        Assert.Same(exception, progress.Snapshot.Error);
    }

    [Fact]
    public void UpdatesAreRejectedAfterClosing()
    {
        var progress = new StartupProgressState();
        progress.Close();

        Assert.True(progress.Snapshot.IsClosed);
        Assert.Throws<InvalidOperationException>(() =>
            progress.ReportStarted(StartupModule.MigrationAndConfiguration, "migración"));
        Assert.Throws<InvalidOperationException>(() => progress.ReportCompleted(StartupModule.MigrationAndConfiguration));
        Assert.Throws<InvalidOperationException>(() => progress.ReportError(StartupModule.MigrationAndConfiguration, "error"));
    }

    [Fact]
    public void ChangedEventReceivesReadyAndErrorTransitions()
    {
        var progress = new StartupProgressState();
        var states = new List<StartupProgressStatus>();
        progress.Changed += snapshot => states.Add(snapshot.Status);

        progress.ReportStarted(StartupModule.MigrationAndConfiguration, "migración");
        progress.ReportError(StartupModule.MigrationAndConfiguration, "fallo");

        Assert.Equal(
            new[] { StartupProgressStatus.InProgress, StartupProgressStatus.Failed },
            states);
    }
}
