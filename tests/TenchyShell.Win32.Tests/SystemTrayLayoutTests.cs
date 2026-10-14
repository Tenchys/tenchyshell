using TenchyShell.Core.SystemTray;
using TenchyShell.Win32;
using Xunit;

namespace TenchyShell.Win32.Tests;

public sealed class SystemTrayLayoutTests
{
    [Fact]
    public void GetAvailableTextWidthSubtractsHorizontalPadding()
    {
        Assert.Equal(152, SystemTrayLayout.GetAvailableTextWidth(200));
        Assert.Equal(0, SystemTrayLayout.GetAvailableTextWidth(40));
    }

    [Fact]
    public void BuildRowTextIncludesSelectionPrefix()
    {
        var item = new SystemTrayItem("id", "Title", "Description");

        Assert.Equal("  Title — Description", SystemTrayLayout.BuildRowText(item, selected: false));
        Assert.Equal("> Title — Description", SystemTrayLayout.GetRowText(item, selected: true));
    }

    [Fact]
    public void MeasureRowHeightUsesMinimumHeightForShortText()
    {
        using var helper = new MeasurementDeviceContext();
        var height = SystemTrayLayout.MeasureRowHeight(helper.Handle, "short", 200);

        Assert.True(height >= SystemTrayLayout.MinimumRowHeight);
    }

    [Fact]
    public void BuildRowLayoutsKeepsCumulativeBoundaries()
    {
        var state = new SystemTrayState(new[]
        {
            new SystemTrayItem("a", "A", "Short text"),
            new SystemTrayItem("b", "B", "A somewhat longer description that should wrap."),
        });

        var layouts = SystemTrayLayout.BuildRowLayouts(state, firstRowTop: 42, index => index == 0 ? 30 : 42);

        Assert.Equal(2, layouts.Count);
        Assert.Equal(0, layouts[0].Index);
        Assert.Equal(42, layouts[0].Top);
        Assert.Equal(72, layouts[0].Bottom);
        Assert.Equal(1, layouts[1].Index);
        Assert.Equal(72, layouts[1].Top);
        Assert.Equal(114, layouts[1].Bottom);
    }

    [Fact]
    public void TryGetItemIndexMatchesRenderedRowBoundaries()
    {
        var layouts = SystemTrayLayout.BuildRowLayouts(firstRowTop: 42, rowHeights: new[] { 30, 50 });

        Assert.True(SystemTrayLayout.TryGetItemIndex(layouts, 45, out var first));
        Assert.Equal(0, first);
        Assert.True(SystemTrayLayout.TryGetItemIndex(layouts, 75, out var second));
        Assert.Equal(1, second);
        Assert.False(SystemTrayLayout.TryGetItemIndex(layouts, 12, out _));
        Assert.False(SystemTrayLayout.TryGetItemIndex(layouts, 200, out _));
    }

    [Fact]
    public void GetPopupHeightSumsMeasuredRowsAndKeepsFooter()
    {
        var layouts = SystemTrayLayout.BuildRowLayouts(firstRowTop: 42, rowHeights: new[] { 30, 30 });

        var height = SystemTrayLayout.GetPopupHeight(configurationHeight: 92, firstRowTop: 42, maxPopupHeight: 620, layouts);

        Assert.Equal(Math.Min(620, 42 + 60 + 28), height);
    }
}

internal sealed class MeasurementDeviceContext : IDisposable
{
    public IntPtr Handle { get; }

    public MeasurementDeviceContext()
    {
        Handle = NativeMethods.GetDC(IntPtr.Zero);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, Handle);
        }
    }
}