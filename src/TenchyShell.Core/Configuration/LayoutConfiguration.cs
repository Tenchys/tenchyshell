using TenchyShell.Core.Layout;

namespace TenchyShell.Core.Configuration;

public sealed class LayoutConfiguration
{
    public bool Enabled { get; init; } = true;

    public int MaxZones { get; init; } = 9;

    public string DefaultPreset { get; init; } = "1x2";

    /// <summary>
    /// Porcentaje del lado menor del área de trabajo usado como altura de los números del overlay.
    /// </summary>
    public double ZoneNumberSizePercent { get; init; } = 4.0;

    public IReadOnlyList<LayoutZone> Zones { get; init; } = Array.Empty<LayoutZone>();
}

public sealed class LayoutHotkeyConfiguration
{
    public IReadOnlyList<string> Zones { get; init; } = Enumerable.Range(1, 9)
        .Select(index => $"Ctrl+Win+{index}")
        .ToArray();

    public string DragModifier { get; init; } = "Ctrl+Shift";
}
