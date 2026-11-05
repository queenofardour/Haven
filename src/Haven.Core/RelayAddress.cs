using System.Net;
using System.Net.Sockets;

namespace Haven.Core;

/// <summary>Where a relay connection comes from, for per-address limits.</summary>
public static class RelayAddress
{
    /// <summary>
    /// An IPv4 address, or an IPv6 address's /64 (one subscriber's network). Empty for a local connection with no
    /// forwarded address (development, tests, a proxy that adds none): those are not limited.
    /// </summary>
    public static string Key(string forwarded, IPAddress? remote)
    {
        var address = IPAddress.TryParse(forwarded.Trim(), out var parsed) ? parsed : remote;
        if (address == null || parsed == null && IPAddress.IsLoopback(address)) return "";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        return Convert.ToHexString(address.GetAddressBytes(), 0, 8) + "/64";
    }
}
