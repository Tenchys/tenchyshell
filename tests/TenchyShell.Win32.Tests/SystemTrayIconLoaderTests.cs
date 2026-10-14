using TenchyShell.Core.Diagnostics;
using Xunit;

namespace TenchyShell.Win32.Tests;

public sealed class SystemTrayIconLoaderTests
{
    [Fact]
    public void ClassifyPathReturnsNotConfiguredForNullOrWhitespace()
    {
        Assert.Equal(SystemTrayIconPathStatus.NotConfigured, SystemTrayIconLoader.ClassifyPath(null));
        Assert.Equal(SystemTrayIconPathStatus.NotConfigured, SystemTrayIconLoader.ClassifyPath("  "));
    }

    [Fact]
    public void ClassifyPathReturnsInvalidExtensionForNonIcoPath()
    {
        var path = CreateTemporaryPath(".png");

        try
        {
            Assert.Equal(SystemTrayIconPathStatus.InvalidExtension, SystemTrayIconLoader.ClassifyPath(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClassifyPathReturnsMissingFileForNonexistentIcoPath()
    {
        var path = CreateTemporaryPath(".ico");

        try
        {
            Assert.False(File.Exists(path));
            Assert.Equal(SystemTrayIconPathStatus.MissingFile, SystemTrayIconLoader.ClassifyPath(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClassifyPathReturnsLoadableForExistingIcoPath()
    {
        var path = CreateTemporaryPath(".ico");

        try
        {
            File.WriteAllBytes(path, [0, 0, 1, 0]);

            Assert.Equal(SystemTrayIconPathStatus.Loadable, SystemTrayIconLoader.ClassifyPath(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClassifyLoadResultReturnsLoadedForValidIconHandle()
    {
        var result = SystemTrayIconLoader.ClassifyLoadResult(
            SystemTrayIconPathStatus.Loadable,
            new IntPtr(1));

        Assert.Equal(SystemTrayIconLoadStatus.Loaded, result);
    }

    [Fact]
    public void ClassifyLoadResultReturnsFailedForZeroIconHandle()
    {
        var result = SystemTrayIconLoader.ClassifyLoadResult(
            SystemTrayIconPathStatus.Loadable,
            IntPtr.Zero);

        Assert.Equal(SystemTrayIconLoadStatus.Failed, result);
    }

    [Fact]
    public void ClassifyPathReflectsAnIconPathChangeAcrossRefreshes()
    {
        var path = CreateTemporaryPath(".ico");

        try
        {
            File.WriteAllBytes(path, [0, 0, 1, 0]);
            Assert.Equal(SystemTrayIconPathStatus.Loadable, SystemTrayIconLoader.ClassifyPath(path));

            File.Delete(path);
            Assert.Equal(SystemTrayIconPathStatus.MissingFile, SystemTrayIconLoader.ClassifyPath(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"TenchyShell-SystemTrayIconLoader-{Guid.NewGuid():N}{extension}");
}
