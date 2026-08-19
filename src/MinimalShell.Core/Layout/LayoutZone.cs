using MinimalShell.Core.Windows;

namespace MinimalShell.Core.Layout;

public readonly record struct LayoutZone(
    int Number,
    string Monitor,
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public bool IsNormalized =>
        Left >= 0 && Left <= 1 &&
        Top >= 0 && Top <= 1 &&
        Right >= 0 && Right <= 1 &&
        Bottom >= 0 && Bottom <= 1 &&
        Left < Right && Top < Bottom;

    public bool Contains(double x, double y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

public sealed class LayoutZoneCatalog
{
    private readonly IReadOnlyList<LayoutZone> configuredZones;
    private readonly int maxZones;

    public LayoutZoneCatalog(IEnumerable<LayoutZone>? zones = null, int maxZones = 9)
    {
        configuredZones = (zones ?? Array.Empty<LayoutZone>()).ToArray();
        this.maxZones = maxZones;
    }

    public IReadOnlyList<LayoutZone> GetZonesForMonitor(string monitorId, bool isPrimary)
    {
        var exact = configuredZones
            .Where(zone => string.Equals(zone.Monitor, monitorId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exact.Length > 0)
        {
            return LimitAndSort(exact);
        }

        var primary = configuredZones
            .Where(zone => isPrimary && string.Equals(zone.Monitor, "primary", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (primary.Length > 0)
        {
            return LimitAndSort(primary);
        }

        var wildcard = configuredZones
            .Where(zone => string.Equals(zone.Monitor, "*", StringComparison.Ordinal))
            .ToArray();

        return wildcard.Length > 0
            ? LimitAndSort(wildcard)
            : LayoutZoneCatalog.CreateDefault1x2();
    }

    public bool TryGetZone(string monitorId, bool isPrimary, int number, out LayoutZone zone)
    {
        zone = GetZonesForMonitor(monitorId, isPrimary)
            .FirstOrDefault(candidate => candidate.Number == number);
        return zone.Number == number;
    }

    public static IReadOnlyList<LayoutZone> CreateDefault1x2() => new[]
    {
        new LayoutZone(1, "*", 0, 0, 0.5, 1),
        new LayoutZone(2, "*", 0.5, 0, 1, 1)
    };

    private IReadOnlyList<LayoutZone> LimitAndSort(IEnumerable<LayoutZone> zones) => zones
        .Where(zone => zone.Number >= 1 && zone.Number <= maxZones)
        .OrderBy(zone => zone.Number)
        .ToArray();
}

public readonly record struct LayoutValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class LayoutZoneValidator
{
    public static LayoutValidationResult Validate(IEnumerable<LayoutZone> zones, int maxZones = 9)
    {
        var errors = new List<string>();
        var materialized = zones.ToArray();

        if (maxZones is < 1 or > 9)
        {
            errors.Add("'layout.max_zones' debe estar entre 1 y 9.");
        }

        foreach (var zone in materialized)
        {
            if (string.IsNullOrWhiteSpace(zone.Monitor))
            {
                errors.Add($"La zona {zone.Number} debe indicar un monitor.");
            }

            if (zone.Number < 1 || zone.Number > maxZones)
            {
                errors.Add($"La zona {zone.Number} está fuera del rango permitido 1..{maxZones}.");
            }

            if (!zone.IsNormalized)
            {
                errors.Add($"La geometría de la zona {zone.Number} no es válida; debe estar entre 0 y 1 y tener ancho y alto positivos.");
            }
        }

        foreach (var group in materialized.GroupBy(zone => zone.Monitor, StringComparer.OrdinalIgnoreCase))
        {
            var duplicates = group
                .GroupBy(zone => zone.Number)
                .Where(numberGroup => numberGroup.Count() > 1)
                .Select(numberGroup => numberGroup.Key);

            foreach (var number in duplicates)
            {
                errors.Add($"El monitor '{group.Key}' tiene repetida la zona {number}.");
            }

            var validZones = group.Where(zone => zone.IsNormalized).ToArray();
            for (var index = 0; index < validZones.Length; index++)
            {
                for (var otherIndex = index + 1; otherIndex < validZones.Length; otherIndex++)
                {
                    if (HasPositiveIntersection(validZones[index], validZones[otherIndex]))
                    {
                        errors.Add($"Las zonas {validZones[index].Number} y {validZones[otherIndex].Number} del monitor '{group.Key}' se superponen.");
                    }
                }
            }
        }

        return new LayoutValidationResult(errors);
    }

    private static bool HasPositiveIntersection(LayoutZone first, LayoutZone second) =>
        Math.Min(first.Right, second.Right) > Math.Max(first.Left, second.Left)
        && Math.Min(first.Bottom, second.Bottom) > Math.Max(first.Top, second.Top);
}

public static class LayoutZoneCalculator
{
    public static WindowRect ToWindowRect(LayoutZone zone, WindowRect workArea)
    {
        var left = workArea.Left + Scale(zone.Left, workArea.Width);
        var top = workArea.Top + Scale(zone.Top, workArea.Height);
        var right = workArea.Left + Scale(zone.Right, workArea.Width);
        var bottom = workArea.Top + Scale(zone.Bottom, workArea.Height);

        return new WindowRect(left, top, right, bottom);
    }

    public static bool TryGetZoneAt(
        IReadOnlyList<LayoutZone> zones,
        double normalizedX,
        double normalizedY,
        out LayoutZone zone)
    {
        zone = zones.FirstOrDefault(candidate => candidate.Contains(normalizedX, normalizedY));
        return zone.Number != 0;
    }

    private static int Scale(double value, int length) =>
        (int)Math.Round(value * length, MidpointRounding.AwayFromZero);
}
