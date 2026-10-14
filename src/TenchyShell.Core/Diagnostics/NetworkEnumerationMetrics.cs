using System.Threading;
using TenchyShell.Core.Network;

namespace TenchyShell.Core.Diagnostics;

/// <summary>
/// Agregados opt-in de la enumeraci��n de red propia de TenchyShell. S��lo
/// contiene contadores y duraciones; nunca SSID, nombres, rutas, HWND ni datos
/// de sesi��n. Los contadores por tipo de interfaz se normalizan a valores no
/// negativos.
/// </summary>
public sealed class NetworkEnumerationMetrics
{
    private long ethernetInterfaces;
    private long wifiInterfaces;
    private long vpnInterfaces;
    private long unknownInterfaces;
    private long wifiNetworksBeforeDeduplication;
    private long uniqueWifiNetworks;
    private long maximumNetworksPerSnapshot;
    private long totalDurationMicroseconds;
    private long interfaceDurationMicroseconds;
    private long wifiDurationMicroseconds;
    private int hasError;

    public NetworkEnumerationMetrics() : this(true)
    {
    }

    public NetworkEnumerationMetrics(bool enabled)
    {
        IsEnabled = enabled;
    }

    public bool IsEnabled { get; }

    public void RecordInterface(NetworkInterfaceKind kind)
    {
        if (!IsEnabled) return;
        switch (kind)
        {
            case NetworkInterfaceKind.Ethernet:
                Interlocked.Increment(ref ethernetInterfaces);
                break;
            case NetworkInterfaceKind.Wifi:
                Interlocked.Increment(ref wifiInterfaces);
                break;
            case NetworkInterfaceKind.Vpn:
                Interlocked.Increment(ref vpnInterfaces);
                break;
            default:
                Interlocked.Increment(ref unknownInterfaces);
                break;
        }
    }

    public void RecordInterfacesCounts(
        long ethernetCount,
        long wifiCount,
        long vpnCount,
        long unknownCount)
    {
        if (!IsEnabled) return;
        Interlocked.Exchange(ref ethernetInterfaces, Math.Max(0, ethernetCount));
        Interlocked.Exchange(ref wifiInterfaces, Math.Max(0, wifiCount));
        Interlocked.Exchange(ref vpnInterfaces, Math.Max(0, vpnCount));
        Interlocked.Exchange(ref unknownInterfaces, Math.Max(0, unknownCount));
    }

    public void RecordWifiNetworks(int beforeDeduplication, int uniqueCount)
    {
        if (!IsEnabled) return;
        Interlocked.Exchange(ref wifiNetworksBeforeDeduplication, Math.Max(0, beforeDeduplication));
        Interlocked.Exchange(ref uniqueWifiNetworks, Math.Max(0, uniqueCount));
        UpdateMaximum(ref maximumNetworksPerSnapshot, Math.Max(0, uniqueCount));
    }

    public void RecordEnumerationDurations(
        TimeSpan interfaceDuration,
        TimeSpan wifiDuration,
        TimeSpan totalDuration)
    {
        if (!IsEnabled) return;
        var interfaceMicroseconds = Math.Max(0, (long)Math.Round(interfaceDuration.TotalMilliseconds * 1000d));
        var wifiMicroseconds = Math.Max(0, (long)Math.Round(wifiDuration.TotalMilliseconds * 1000d));
        var totalMicroseconds = Math.Max(0, (long)Math.Round(totalDuration.TotalMilliseconds * 1000d));
        Interlocked.Exchange(ref interfaceDurationMicroseconds, interfaceMicroseconds);
        Interlocked.Exchange(ref wifiDurationMicroseconds, wifiMicroseconds);
        Interlocked.Exchange(ref totalDurationMicroseconds, totalMicroseconds);
    }

    public void RecordError()
    {
        if (!IsEnabled) return;
        Interlocked.Exchange(ref hasError, 1);
    }

    public NetworkEnumerationMetricsSnapshot Snapshot() => new(
        Math.Max(0, Interlocked.Read(ref ethernetInterfaces)),
        Math.Max(0, Interlocked.Read(ref wifiInterfaces)),
        Math.Max(0, Interlocked.Read(ref vpnInterfaces)),
        Math.Max(0, Interlocked.Read(ref unknownInterfaces)),
        Math.Max(0, Interlocked.Read(ref wifiNetworksBeforeDeduplication)),
        Math.Max(0, Interlocked.Read(ref uniqueWifiNetworks)),
        Math.Max(0, Interlocked.Read(ref maximumNetworksPerSnapshot)),
        Interlocked.Read(ref totalDurationMicroseconds) / 1000d,
        Interlocked.Read(ref interfaceDurationMicroseconds) / 1000d,
        Interlocked.Read(ref wifiDurationMicroseconds) / 1000d,
        Volatile.Read(ref hasError) == 1);

    private static void UpdateMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}

/// <summary>Instantanea agregada y segura de una enumeraci��n de red.</summary>
public readonly record struct NetworkEnumerationMetricsSnapshot(
    long EthernetInterfaces,
    long WifiInterfaces,
    long VpnInterfaces,
    long UnknownInterfaces,
    long WifiNetworksBeforeDeduplication,
    long UniqueWifiNetworks,
    long MaximumNetworksPerSnapshot,
    double TotalDurationMilliseconds,
    double InterfaceEnumerationDurationMilliseconds,
    double WifiEnumerationDurationMilliseconds,
    bool HasError)
{
}
