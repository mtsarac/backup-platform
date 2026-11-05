using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.S3;

/// <summary>
/// The address rule for S3 traffic that leaves the API process (connection test, downloads, retention deletes, verification, repair). It resolves the endpoint host once, requires EVERY answer
/// to be a public address or inside an operator-allowed network, and connects to one of the checked addresses itself, so a DNS answer that changes after the
/// check (rebinding) cannot reach a private host. Agent-executed transfers do not use it: the agent sits on the customer's network, where a private S3
/// endpoint is the normal case.
/// </summary>
public sealed class S3NetworkPolicy
{
    private static readonly IPNetwork[] Restricted = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16",
        "192.88.99.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "::/96", "64:ff9b:1::/48", "100::/64", "2001::/32", "2001:db8::/32", "3fff::/20", "5f00::/16", "fc00::/7", "fe80::/10", "fec0::/10", "ff00::/8"
    }.Select(IPNetwork.Parse).ToArray();

    private readonly IPNetwork[] _allowed;
    private readonly TimeSpan _connectTimeout;

    public S3NetworkPolicy(IEnumerable<string> allowedPrivateNetworks, TimeSpan connectTimeout)
    {
        ArgumentNullException.ThrowIfNull(allowedPrivateNetworks);
        if (connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        _allowed = allowedPrivateNetworks.Select(IPNetwork.Parse).ToArray();
        _connectTimeout = connectTimeout;
    }

    /// <summary>A handler that sends nothing through a proxy, follows no redirect and connects only through <see cref="ConnectAsync"/>.</summary>
    public SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ConnectAsync };

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

    /// <summary>The permitted addresses of <paramref name="host"/>. Throws <see cref="StorageException"/> (NetworkDenied) when any answer is not permitted.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        IPAddress[] answers;
        if (IPAddress.TryParse(literal, out var parsed)) answers = [parsed];
        else
        {
            try { answers = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false); }
            catch (SocketException) { throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 endpoint host could not be resolved."); }
        }
        if (answers.Length == 0) throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 endpoint host could not be resolved.");
        var normalized = answers.Select(Normalize).ToArray();
        // One forbidden answer refuses the whole name: a resolver mixing public and private records must not get to pick the private one.
        if (normalized.Any(address => IsRestricted(address) && !_allowed.Any(network => network.Contains(address))))
            throw new StorageException(StorageFailureKind.NetworkDenied, "Access to this network destination is not permitted by operator policy.");
        return normalized;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_connectTimeout);
        try
        {
            var addresses = await ResolveAsync(context.DnsEndPoint.Host, limit.Token).ConfigureAwait(false);
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), limit.Token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException exception) { socket.Dispose(); last = exception; }
                catch { socket.Dispose(); throw; }
            }
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 storage is unavailable.", last);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StorageException(StorageFailureKind.Timeout, "The S3 connection timed out.");
        }
    }
}
