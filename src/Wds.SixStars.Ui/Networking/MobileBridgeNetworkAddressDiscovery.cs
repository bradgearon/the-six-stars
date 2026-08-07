using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Wds.SixStars.Ui.Networking;

/// <summary>
/// Describes the active desktop network path used by one Android bridge socket. The preferred
/// IPv6 address is selected from the same adapter that owns the socket's local endpoint, so a
/// Wi-Fi connection does not accidentally advertise an address from Ethernet, a VPN, Hyper-V,
/// or another inactive interface.
/// </summary>
internal sealed class MobileBridgeConnectionNetworkInfo
{
    public string LocalAddress { get; init; } = string.Empty;
    public string RemoteAddress { get; init; } = string.Empty;
    public string AddressFamily { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string AdapterDescription { get; init; } = string.Empty;
    public bool IsLocalConnection { get; init; }
    public string PreferredIpv6Address { get; init; } = string.Empty;
    public string PreferredIpv6Host { get; init; } = string.Empty;
    public string PreferredIpv6BridgeUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> PreferredIpv6Addresses { get; init; } = Array.Empty<string>();
    public string DiscoverySource { get; init; } = "active-socket-adapter";
    public bool HasPreferredIpv6 => !string.IsNullOrWhiteSpace(PreferredIpv6Address);
}

internal static class MobileBridgeNetworkAddressDiscovery
{
    public static MobileBridgeConnectionNetworkInfo Discover(TcpClient client, int bridgePort)
    {
        IPEndPoint? localEndPoint = client.Client.LocalEndPoint as IPEndPoint;
        IPEndPoint? remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        IPAddress? localAddress = NormalizeAddress(localEndPoint?.Address);
        IPAddress? remoteAddress = NormalizeAddress(remoteEndPoint?.Address);
        NetworkInterface? adapter = FindOwningAdapter(localAddress);
        bool isLocalConnection = IsPrivateOrLocal(remoteAddress) || IsOnSameAdapterSubnet(adapter, remoteAddress);
        List<IPAddress> preferredIpv6Candidates = isLocalConnection
            ? SelectPreferredIpv6Candidates(adapter, localAddress)
            : new List<IPAddress>();
        IPAddress? preferredIpv6 = preferredIpv6Candidates.FirstOrDefault();
        string ipv6Text = preferredIpv6?.ToString() ?? string.Empty;
        return new MobileBridgeConnectionNetworkInfo
        {
            LocalAddress = localAddress?.ToString() ?? string.Empty,
            RemoteAddress = remoteAddress?.ToString() ?? string.Empty,
            AddressFamily = localAddress?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4",
            AdapterName = adapter?.Name ?? string.Empty,
            AdapterDescription = adapter?.Description ?? string.Empty,
            IsLocalConnection = isLocalConnection,
            PreferredIpv6Address = ipv6Text,
            PreferredIpv6Host = ipv6Text,
            PreferredIpv6BridgeUrl = string.IsNullOrWhiteSpace(ipv6Text)
                ? string.Empty
                : $"http://[{ipv6Text}]:{bridgePort}",
            PreferredIpv6Addresses = preferredIpv6Candidates.Select(x => x.ToString()).ToArray(),
            DiscoverySource = adapter == null ? "active-socket-endpoint" : "active-socket-adapter"
        };
    }

    private static NetworkInterface? FindOwningAdapter(IPAddress? localAddress)
    {
        if (localAddress == null) return null;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsableAdapter)
                .FirstOrDefault(networkInterface => networkInterface.GetIPProperties().UnicastAddresses
                    .Any(unicast => AddressesEqual(NormalizeAddress(unicast.Address), localAddress)));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsableAdapter(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up) return false;
        return networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel;
    }

    private static List<IPAddress> SelectPreferredIpv6Candidates(NetworkInterface? adapter, IPAddress? activeLocalAddress)
    {
        List<IPAddress> result = new List<IPAddress>();
        if (activeLocalAddress?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && IsUsableAdvertisedIpv6(activeLocalAddress))
        {
            result.Add(activeLocalAddress);
        }
        if (adapter == null) return result;

        try
        {
            IEnumerable<IPAddress> candidates = adapter.GetIPProperties().UnicastAddresses
                .Where(unicast => IsUsableAdvertisedIpv6(unicast.Address))
                .OrderBy(ScoreIpv6Candidate)
                .ThenBy(unicast => unicast.Address.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(unicast => unicast.Address);
            foreach (IPAddress candidate in candidates)
            {
                if (!result.Contains(candidate)) result.Add(candidate);
            }
        }
        catch
        {
        }
        return result;
    }

    private static int ScoreIpv6Candidate(UnicastIPAddressInformation candidate)
    {
        IPAddress address = candidate.Address;
        if (!IsUniqueLocalIpv6(address))
        {
            return candidate.SuffixOrigin switch
            {
                SuffixOrigin.Manual => 0,
                SuffixOrigin.OriginDhcp => 0,
                SuffixOrigin.LinkLayerAddress => 1,
                SuffixOrigin.WellKnown => 1,
                SuffixOrigin.Random => 3,
                _ => 2
            };
        }
        return 2;
    }

    private static bool IsUsableAdvertisedIpv6(IPAddress? address)
    {
        if (address == null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        if (address.IsIPv4MappedToIPv6 || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        if (IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6None.Equals(address) || IPAddress.IPv6Loopback.Equals(address)) return false;
        return true;
    }

    private static bool IsOnSameAdapterSubnet(NetworkInterface? adapter, IPAddress? remoteAddress)
    {
        if (adapter == null || remoteAddress == null) return false;
        remoteAddress = NormalizeAddress(remoteAddress);
        if (remoteAddress == null) return false;
        try
        {
            foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                IPAddress? local = NormalizeAddress(unicast.Address);
                if (local == null || local.AddressFamily != remoteAddress.AddressFamily) continue;
                int prefixLength = unicast.PrefixLength;
                if (prefixLength <= 0) continue;
                if (SharesPrefix(local.GetAddressBytes(), remoteAddress.GetAddressBytes(), prefixLength)) return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool SharesPrefix(byte[] left, byte[] right, int prefixLength)
    {
        if (left.Length != right.Length || prefixLength < 0 || prefixLength > left.Length * 8) return false;
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        for (int i = 0; i < fullBytes; i++) if (left[i] != right[i]) return false;
        if (remainingBits == 0) return true;
        int mask = 0xFF << (8 - remainingBits);
        return (left[fullBytes] & mask) == (right[fullBytes] & mask);
    }

    private static bool IsPrivateOrLocal(IPAddress? address)
    {
        if (address == null) return false;
        address = NormalizeAddress(address);
        if (address == null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || IsUniqueLocalIpv6(address);
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4
            && (bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254));
    }

    private static bool IsUniqueLocalIpv6(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static IPAddress? NormalizeAddress(IPAddress? address)
    {
        return address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
    }

    private static bool AddressesEqual(IPAddress? left, IPAddress? right)
    {
        if (left == null || right == null) return false;
        return left.Equals(right);
    }
}
