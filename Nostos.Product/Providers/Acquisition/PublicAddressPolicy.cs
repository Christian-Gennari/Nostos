using System.Net;
using System.Net.Sockets;

namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// The half of the SSRF boundary that is about addresses rather than names.
///
/// <see cref="ProviderHostPolicy"/> decides which hosts a provider may name.
/// A user-supplied cover URL is not a provider: the host is whatever the caller
/// wrote, so naming it proves nothing. This decides where such a URL may land —
/// nowhere loopback, private, link-local or otherwise not routable. On a
/// multi-tenant host that difference is the whole boundary: without it a caller
/// could make Nostos fetch <c>https://127.0.0.1/…</c>, a neighbour on
/// <c>10.0.0.5</c>, or the cloud metadata service on <c>169.254.169.254</c>.
///
/// Classification is a pure function so it is testable without a socket, and
/// the same function guards the connection itself
/// (<see cref="ConnectToPublicAsync"/>): resolving a name, approving it, then
/// letting the platform resolve it again is exactly the DNS-rebinding hole this
/// closes.
/// </summary>
public static class PublicAddressPolicy
{
    /// <summary>The reason a refused URL is given, and what the connect guard throws.</summary>
    public const string RefusalReason = "the URL points to a private or local address";

    /// <summary>
    /// True only for an address that is globally routable. An IPv4-mapped IPv6
    /// address is classified as the IPv4 address it carries, so
    /// <c>::ffff:127.0.0.1</c> cannot be used to reach loopback.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsPublic(address.MapToIPv4());

            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
                return false;

            var octets = address.GetAddressBytes();

            // The deprecated IPv4-compatible form (::a.b.c.d) carries IPv4 too.
            if (octets[..12].All(octet => octet == 0))
                return IsPublic(new IPAddress(octets[12..]));

            // fc00::/7 unique local.
            if ((octets[0] & 0xfe) == 0xfc)
                return false;

            // fe80::/10 link-local, and the deprecated fec0::/10 site-local.
            if (octets[0] == 0xfe && (octets[1] & 0xc0) != 0)
                return false;

            // ff00::/8 multicast.
            return octets[0] != 0xff;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var v4 = address.GetAddressBytes();
        return v4[0] switch
        {
            0 => false, // 0.0.0.0/8, "this network"
            10 => false, // 10.0.0.0/8 private
            127 => false, // 127.0.0.0/8 loopback
            169 when v4[1] == 254 => false, // 169.254.0.0/16 link-local, and cloud metadata
            172 when v4[1] is >= 16 and <= 31 => false, // 172.16.0.0/12 private
            192 when v4[1] == 168 => false, // 192.168.0.0/16 private
            100 when v4[1] is >= 64 and <= 127 => false, // 100.64.0.0/10 carrier-grade NAT
            >= 224 => false, // multicast (224/4), reserved (240/4) and broadcast
            _ => true,
        };
    }

    /// <summary>True for <c>localhost</c> and every name under it.</summary>
    public static bool IsLocalHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        var name = host.Trim().TrimEnd('.');
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> once and reports whether every address
    /// it names is public. A name that does not resolve is allowed through:
    /// there is nothing to reach, and the fetch itself reports the failure.
    /// </summary>
    public static async Task<bool> IsSafeToFetchAsync(string host, CancellationToken ct)
    {
        if (IsLocalHostName(host))
            return false;

        var name = Normalise(host);
        if (IPAddress.TryParse(name, out var literal))
            return IsPublic(literal);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, ct);
            return addresses.Length > 0 && addresses.All(IsPublic);
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// The <c>ConnectCallback</c> of the client that fetches a user-supplied
    /// cover URL: every connection it opens — including one made to follow a
    /// redirect — goes through <see cref="ConnectToPublicAsync"/>.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct) =>
        await ConnectToPublicAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);

    /// <summary>
    /// Resolves <paramref name="host"/> and connects only to an address
    /// <see cref="IsPublic"/> accepted, using that same address — never a second
    /// resolution the caller cannot see. Throws before opening a socket when
    /// any address is not public.
    /// </summary>
    public static async ValueTask<Stream> ConnectToPublicAsync(string host, int port, CancellationToken ct)
    {
        var name = Normalise(host);

        IPAddress[] addresses;
        if (IPAddress.TryParse(name, out var literal))
            addresses = [literal];
        else
            addresses = await Dns.GetHostAddressesAsync(name, ct);

        if (addresses.Length == 0)
            throw new HttpRequestException($"'{host}' did not resolve to an address.");

        if (addresses.Any(address => !IsPublic(address)))
            throw new HttpRequestException(RefusalReason);

        Exception? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                failure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"Could not open a connection to '{host}'.", failure);
    }

    /// <summary>A URI host may arrive bracketed (<c>[::1]</c>); an address parser wants it bare.</summary>
    private static string Normalise(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
}
