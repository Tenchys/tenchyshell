using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TenchyShell.Core.Network;

namespace TenchyShell.Win32;

public sealed class NetworkService : INetworkService
{
    private const uint WlanClientVersion = 2;
    private const int WlanInterfaceInfoListHeaderSize = 8;
    private const int WlanAvailableNetworkIncludeAllAdhocProfiles = 0x00000001;

    public NetworkSnapshot GetSnapshot()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(CreateInterfaceSnapshot)
                .ToArray();

            var operational = interfaces.Any(networkInterface => networkInterface.IsOperational);
            return new NetworkSnapshot(operational, operational, interfaces, null)
            {
                WifiNetworks = GetWifiNetworks()
            };
        }
        catch (Exception exception)
        {
            return new NetworkSnapshot(false, false, Array.Empty<NetworkInterfaceSnapshot>(), exception.Message);
        }
    }

    public NetworkOperationResult OpenNativeSettings() => OpenSettings("ms-settings:network");

    public NetworkOperationResult OpenWifiSettings(string? ssid) => OpenSettings("ms-settings:network-wifi");

    public NetworkOperationResult ConnectWifi(string ssid) => RunNetsh("wlan", "connect", $"name={ssid}");

    public NetworkOperationResult DisconnectWifi() => RunNetsh("wlan", "disconnect");

    public NetworkOperationResult SetInterfaceEnabled(string interfaceName, bool enabled) =>
        RunNetsh(new[] { "interface", "set", "interface", $"name={interfaceName}", $"admin={(enabled ? "enabled" : "disabled")}" }, elevated: true);

    private static NetworkInterfaceSnapshot CreateInterfaceSnapshot(NetworkInterface networkInterface)
    {
        var addresses = networkInterface.GetIPProperties().UnicastAddresses
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NetworkInterfaceSnapshot(
            networkInterface.Id,
            networkInterface.Name,
            MapKind(networkInterface.NetworkInterfaceType, networkInterface.Description),
            networkInterface.OperationalStatus.ToString(),
            networkInterface.Speed,
            networkInterface.OperationalStatus == OperationalStatus.Up)
        {
            IpAddresses = addresses
        };
    }

    private static NetworkOperationResult OpenSettings(string uri)
    {
        try
        {
            // ShellExecute(ms-settings:...) puede depender de Explorer. Lanzamos
            // SystemSettings.exe directamente para que el shell siga vivo sin Explorer.
            var settingsExecutable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ImmersiveControlPanel",
                "SystemSettings.exe");
            if (!File.Exists(settingsExecutable))
            {
                return NetworkOperationResult.Failure("No se encontró SystemSettings.exe; no se usará Explorer como fallback.");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = settingsExecutable,
                Arguments = uri,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(settingsExecutable)!
            });
            return NetworkOperationResult.Success();
        }
        catch (Exception exception)
        {
            return NetworkOperationResult.Failure($"No se pudo abrir la configuración de red: {exception.Message}");
        }
    }

    private static NetworkOperationResult RunNetsh(params string[] arguments)
    {
        return RunNetsh(arguments, elevated: false);
    }

    private static NetworkOperationResult RunNetsh(string[] arguments, bool elevated)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (elevated)
            {
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
            }
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
            return NetworkOperationResult.Success();
        }
        catch (Exception exception)
        {
            return NetworkOperationResult.Failure($"No se pudo ejecutar la operación de red: {exception.Message}");
        }
    }

    private static IReadOnlyList<WifiNetworkSnapshot> GetWifiNetworks()
    {
        var networks = new List<WifiNetworkSnapshot>();
        var result = WlanOpenHandle(WlanClientVersion, IntPtr.Zero, out _, out var clientHandle);
        if (result != 0) return networks;

        try
        {
            result = WlanEnumInterfaces(clientHandle, IntPtr.Zero, out var interfaceListPointer);
            if (result != 0 || interfaceListPointer == IntPtr.Zero) return networks;

            try
            {
                var count = Marshal.ReadInt32(interfaceListPointer);
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
                for (var index = 0; index < count; index++)
                {
                    var interfacePointer = IntPtr.Add(interfaceListPointer,
                        WlanInterfaceInfoListHeaderSize + index * itemSize);
                    var wlanInterface = Marshal.PtrToStructure<WlanInterfaceInfo>(interfacePointer);
                    result = WlanGetAvailableNetworkList(
                        clientHandle,
                        ref wlanInterface.InterfaceGuid,
                        WlanAvailableNetworkIncludeAllAdhocProfiles,
                        IntPtr.Zero,
                        out var networkListPointer);
                    if (result != 0 || networkListPointer == IntPtr.Zero) continue;

                    try
                    {
                        var networkCount = Marshal.ReadInt32(networkListPointer);
                        var networkItemSize = Marshal.SizeOf<WlanAvailableNetwork>();
                        for (var networkIndex = 0; networkIndex < networkCount; networkIndex++)
                        {
                            var networkPointer = IntPtr.Add(networkListPointer,
                                WlanInterfaceInfoListHeaderSize + networkIndex * networkItemSize);
                            var network = Marshal.PtrToStructure<WlanAvailableNetwork>(networkPointer);
                            var ssid = network.Ssid.GetText();
                            if (string.IsNullOrWhiteSpace(ssid)) continue;

                            networks.Add(new WifiNetworkSnapshot(
                                ssid,
                                (int)Math.Clamp(network.SignalQuality, 0, 100),
                                network.SecurityEnabledFlag != 0,
                                (network.Flags & 0x00000001) != 0));
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(networkListPointer);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(interfaceListPointer);
            }
        }
        finally
        {
            WlanCloseHandle(clientHandle, IntPtr.Zero);
        }

        return networks
            .GroupBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(network => network.SignalQuality).First())
            .OrderByDescending(network => network.IsConnected)
            .ThenByDescending(network => network.SignalQuality)
            .ToArray();
    }

    private static NetworkInterfaceKind MapKind(NetworkInterfaceType type, string description)
    {
        if (description.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("WFP", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("QoS", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Teredo", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("IP-HTTPS", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("6to4", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkInterfaceKind.Vpn;
        }

        if (type == NetworkInterfaceType.Wireless80211) return NetworkInterfaceKind.Wifi;
        if (type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.GigabitEthernet)
        {
            return NetworkInterfaceKind.Ethernet;
        }

        return NetworkInterfaceKind.Unknown;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid, int flags, IntPtr reserved, out IntPtr availableNetworkList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        internal Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string InterfaceDescription;

        internal int InterfaceState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAvailableNetwork
    {
        internal WlanSsid Ssid;
        internal int BssType;
        internal uint NumberOfBssids;
        internal int NetworkConnectable;
        internal int NotConnectableReason;
        internal uint NumberOfPhyTypes;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        internal int[]? PhyTypes;

        internal int MorePhyTypes;
        internal uint SignalQuality;
        internal int SecurityEnabledFlag;
        internal int DefaultAuthAlgorithm;
        internal int DefaultCipherAlgorithm;
        internal uint Flags;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSsid
    {
        internal uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[]? Bytes;

        internal string GetText()
        {
            if (Bytes is null || Length == 0) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(Bytes, 0, (int)Math.Min(Length, (uint)Bytes.Length));
        }
    }
}
