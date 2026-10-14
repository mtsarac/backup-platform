using System.Net;
using System.Net.Sockets;

namespace BackupPlatform.Notifications;

/// <summary>Why outbound notification traffic was refused or failed before any protocol exchange. The code is stable; it never carries the host or an address.</summary>
public sealed class NotificationNetworkException(string code, Exception? inner = null) : Exception("Outbound notification network failure: " + code, inner)
{
    public const string Denied = "network_denied";
    public const string DnsFailed = "dns_failed";
    public const string ConnectFailed = "connect_failed";

    public string Code { get; } = code;
}

/// <summary>
/// The single door outbound notification traffic (tenant webhooks, the SMTP relay, the SMS gateway) goes through. It resolves a host, requires
/// EVERY answer to be a public address (or inside an operator-allowed CIDR), and connects to one of the validated addresses itself. Because the
/// caller connects through <see cref="ConnectAsync"/> and never resolves again, the address that was checked is the address that is used: a DNS
/// answer that changes between "check" and "connect" (rebinding) cannot reach a private host.
/// </summary>
public sealed class NotificationNetworkGuard(NotificationOptions options)
{
    private static readonly IPNetwork[] Restricted = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16",
        "192.88.99.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "::/96", "64:ff9b:1::/48", "100::/64", "2001::/32", "2001:db8::/32", "3fff::/20", "5f00::/16", "fc00::/7", "fe80::/10", "fec0::/10", "ff00::/8"
    }.Select(IPNetwork.Parse).ToArray();

    private readonly IPNetwork[] _allowed = options.AllowedPrivateNetworks.Select(IPNetwork.Parse).ToArray();

    /// <summary>HTTPS (webhook, SMS gateway) may only reach port 443 or an operator-allowed one; the SMTP relay is the operator's own setting and is not limited here.</summary>
    public bool IsHttpsPortAllowed(int port) => options.IsHttpsPortAllowed(port);

    /// <summary>True for loopback, private, link-local, carrier-grade NAT, multicast, reserved and documentation space, including IPv4 hidden inside an IPv6 address.</summary>
    public static bool IsRestricted(IPAddress address)
    {
        address = Normalize(address);
        return Restricted.Any(network => network.Contains(address));
    }

    /// <summary>Maps IPv4-mapped IPv6 to IPv4 and unwraps the IPv4 embedded in NAT64 (64:ff9b::/96) and 6to4 (2002::/16) so they are judged as the IPv4 they reach.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4..12].All(b => b == 0)) return new IPAddress(bytes[12..16]);
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return new IPAddress(bytes[2..6]);
        return address;
    }

    /// <summary>The permitted addresses of <paramref name="host"/> (a name, an IPv4 literal or a bracketed/unbracketed IPv6 literal). Throws <see cref="NotificationNetworkException"/>.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        IPAddress[] answers;
        if (IPAddress.TryParse(literal, out var parsed))
        {
            answers = [parsed];
        }
        else
        {
            try { answers = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false); }
            catch (SocketException exception) { throw new NotificationNetworkException(NotificationNetworkException.DnsFailed, exception); }
        }
        if (answers.Length == 0) throw new NotificationNetworkException(NotificationNetworkException.DnsFailed);
        var normalized = answers.Select(Normalize).ToArray();
        // One forbidden answer refuses the whole name: a resolver mixing public and private records must not get to pick the private one.
        if (normalized.Any(address => IsRestricted(address) && !_allowed.Any(network => network.Contains(address))))
            throw new NotificationNetworkException(NotificationNetworkException.Denied);
        return normalized;
    }

    /// <summary>A connected socket to one of the validated addresses; the caller owns it. Throws <see cref="NotificationNetworkException"/>.</summary>
    public async Task<Socket> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new NotificationNetworkException(NotificationNetworkException.ConnectFailed, last);
    }
}
