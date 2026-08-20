namespace TenchyShell.Core.Network;

public enum NetworkInterfaceKind
{
    Unknown,
    Ethernet,
    Wifi,
    Vpn,
    Loopback
}

public sealed record NetworkInterfaceSnapshot(
    string Id,
    string Name,
    NetworkInterfaceKind Kind,
    string Status,
    long SpeedBitsPerSecond,
    bool IsOperational)
{
    public IReadOnlyList<string> IpAddresses { get; init; } = Array.Empty<string>();
}

public sealed record WifiNetworkSnapshot(
    string Ssid,
    int SignalQuality,
    bool IsSecured,
    bool IsConnected);

public sealed record NetworkSnapshot(
    bool HasAnyConnection,
    bool HasInternetCandidate,
    IReadOnlyList<NetworkInterfaceSnapshot> Interfaces,
    string? Error)
{
    public IReadOnlyList<WifiNetworkSnapshot> WifiNetworks { get; init; } = Array.Empty<WifiNetworkSnapshot>();
}

public sealed record NetworkOperationResult(bool Succeeded, string? Error)
{
    public static NetworkOperationResult Success() => new(true, null);

    public static NetworkOperationResult Failure(string error) => new(false, error);
}

public interface INetworkService
{
    NetworkSnapshot GetSnapshot();

    NetworkOperationResult OpenNativeSettings();

    NetworkOperationResult OpenWifiSettings(string? ssid);

    NetworkOperationResult ConnectWifi(string ssid);

    NetworkOperationResult DisconnectWifi();

    NetworkOperationResult SetInterfaceEnabled(string interfaceName, bool enabled);
}
