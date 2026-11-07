using System.Net;
using System.Net.Sockets;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>
/// Which addresses a client metadata fetch may connect to (issue #1470): only
/// public unicast ones. Loopback, private (RFC 1918, ULA), link-local,
/// carrier-grade NAT (100.64/10, which Tailscale uses), multicast, reserved,
/// documentation and benchmarking ranges, and the IPv6 forms that carry an
/// IPv4 address (mapped, compatible, NAT64, 6to4, Teredo) are all refused, so
/// a client id cannot make this server read its own network.
/// </summary>
public static class McpNetworkAddressFilter
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address.GetAddressBytes()),
            _ => false,
        };
    }

    private static bool IsPublicV4(byte[] b)
    {
        return !(b[0] == 0                                   // 0.0.0.0/8 "this network"
            || b[0] == 10                                    // 10/8 private
            || (b[0] == 100 && (b[1] & 0xC0) == 64)          // 100.64/10 carrier-grade NAT, Tailscale
            || b[0] == 127                                   // loopback
            || (b[0] == 169 && b[1] == 254)                  // link-local, cloud metadata
            || (b[0] == 172 && (b[1] & 0xF0) == 16)          // 172.16/12 private
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0/24 IETF protocol assignments
            || (b[0] == 192 && b[1] == 0 && b[2] == 2)       // TEST-NET-1
            || (b[0] == 192 && b[1] == 88 && b[2] == 99)     // 6to4 relay anycast
            || (b[0] == 192 && b[1] == 168)                  // 192.168/16 private
            || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18/15 benchmarking
            || (b[0] == 198 && b[1] == 51 && b[2] == 100)    // TEST-NET-2
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)     // TEST-NET-3
            || b[0] >= 224);                                 // multicast, reserved, broadcast
    }

    private static bool IsPublicV6(byte[] b)
    {
        // Only global unicast, 2000::/3, is ever public.
        if ((b[0] & 0xE0) != 0x20)
            return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)   // 2001:db8::/32 documentation
            return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)   // 2001::/32 Teredo
            return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) == 0x10) // 2001:10::/28 ORCHID
            return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) == 0x20) // 2001:20::/28 ORCHIDv2
            return false;
        if (b[0] == 0x20 && b[1] == 0x02)                                    // 2002::/16 6to4
            return false;
        return true;
    }
}
