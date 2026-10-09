using System.Net;
using System.Net.Sockets;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.Sftp;

public static class SftpNetworkValidator
{
    public static async Task<IPAddress> ResolveAndPinAsync(
        string host,
        SftpNetworkOptions network,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(network);

        cancellationToken.ThrowIfCancellationRequested();

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            addresses = [parsedIp];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new StorageException(StorageFailureKind.NetworkUnavailable, "Failed to resolve SFTP host name.", ex);
            }
        }

        if (addresses.Length == 0)
        {
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "No IP addresses resolved for SFTP host.");
        }

        var selected = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.First();

        AssertIpPermitted(selected, network.AllowedPrivateNetworks);

        return selected;
    }

    public static void AssertIpPermitted(IPAddress ip, string[] allowedPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(allowedPrivateNetworks);

        var isPrivate = IsPrivateOrRestricted(ip);
        if (!isPrivate)
        {
            return;
        }

        // Check if matching any explicit operator CIDR
        foreach (var cidr in allowedPrivateNetworks)
        {
            if (IPNetwork.TryParse(cidr, out var network) && network.Contains(ip))
            {
                return;
            }
        }

        throw new StorageException(
            StorageFailureKind.NetworkDenied,
            "Target SFTP IP address belongs to a private, loopback, or link-local network and is not explicitly allowed.");
    }

    public static bool IsPrivateOrRestricted(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 100.64.0.0/10 (Carrier-grade NAT)
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;
            // 127.0.0.0/8 (Loopback)
            if (bytes[0] == 127) return true;
            // 169.254.0.0/16 (Link-local / Cloud metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 192.0.0.0/24 (IETF Protocol Assignments)
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) return true;
            // 198.18.0.0/15 (Benchmarking)
            if (bytes[0] == 198 && (bytes[1] & 0xFE) == 18) return true;
            // 224.0.0.0/4 (Multicast)
            if ((bytes[0] & 0xF0) == 224) return true;
            // 240.0.0.0/4 (Reserved)
            if ((bytes[0] & 0xF0) == 240) return true;
            // 255.255.255.255 (Broadcast)
            if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return true;
        }

        return false;
    }
}
