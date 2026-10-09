using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.Sftp;

public static partial class SftpConfiguration
{
    public const string AuthTypePassword = "password";
    public const string AuthTypePrivateKey = "private-key";

    public static void Validate(SftpStorageOptions options, SftpNetworkOptions? network = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Host) || options.Host.Length > 255)
        {
            throw new ArgumentException("SFTP host must be 1..255 characters.", nameof(options));
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentException("SFTP port must be between 1 and 65535.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Username) || options.Username.Length > 64)
        {
            throw new ArgumentException("SFTP username must be 1..64 characters.", nameof(options));
        }

        if (options.AuthenticationType is not (AuthTypePassword or AuthTypePrivateKey))
        {
            throw new ArgumentException($"SFTP authentication type must be '{AuthTypePassword}' or '{AuthTypePrivateKey}'.", nameof(options));
        }

        ValidateRemoteBaseDirectory(options.RemoteBaseDirectory);
        ValidateHostKeyFingerprint(options.HostKeyFingerprint);

        if (options.TenantId == Guid.Empty)
        {
            throw new ArgumentException("SFTP tenant id cannot be empty.", nameof(options));
        }

        if (options.ConnectionId == Guid.Empty)
        {
            throw new ArgumentException("SFTP connection id cannot be empty.", nameof(options));
        }

        if (network is not null)
        {
            if (network.ConnectionTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException("Connection timeout must be positive.", nameof(network));
            }

            if (network.OperationTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException("Operation timeout must be positive.", nameof(network));
            }

            if (network.TransferTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException("Transfer timeout must be positive.", nameof(network));
            }

            foreach (var cidr in network.AllowedPrivateNetworks)
            {
                if (!IPNetwork.TryParse(cidr, out _))
                {
                    throw new ArgumentException($"Invalid CIDR network in allowlist: {cidr}", nameof(network));
                }
            }
        }
    }

    public static string NormalizeRemoteBaseDirectory(string path)
    {
        ValidateRemoteBaseDirectory(path);
        var trimmed = path.Trim();
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segments);
    }

    public static void ValidateRemoteBaseDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Remote base directory cannot be empty.", nameof(path));
        }

        if (path.Length > 512)
        {
            throw new ArgumentException("Remote base directory exceeds maximum length 512.", nameof(path));
        }

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Remote base directory must be an absolute path starting with '/'.", nameof(path));
        }

        if (path.Any(char.IsControl) || path.Contains('\\'))
        {
            throw new ArgumentException("Remote base directory must not contain control characters or backslashes.", nameof(path));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Remote base directory must not contain traversal segments.", nameof(path));
            }

            if (segment.EndsWith('.'))
            {
                throw new ArgumentException("Remote base directory segments must not end with a dot.", nameof(path));
            }

            if (!segment.All(IsAllowedPathChar))
            {
                throw new ArgumentException("Remote base directory contains invalid characters.", nameof(path));
            }
        }
    }

    public static string NormalizeHostKeyFingerprint(string? fingerprint)
    {
        ValidateHostKeyFingerprint(fingerprint);
        var value = fingerprint!.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }

        return value;
    }

    public static void ValidateHostKeyFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Host key fingerprint cannot be empty.", nameof(fingerprint));
        }

        var trimmed = fingerprint.Trim();
        var base64Part = trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["SHA256:".Length..]
            : trimmed;
        var padded = base64Part.PadRight((base64Part.Length + 3) / 4 * 4, '=');
        Span<byte> buffer = stackalloc byte[32];
        if (!Convert.TryFromBase64String(padded, buffer, out var bytesWritten) || bytesWritten != 32)
        {
            throw new ArgumentException("Host key fingerprint must be a valid standard 32-byte Base64 SHA256 digest.", nameof(fingerprint));
        }
    }

    public static bool MatchesHostKey(string expectedFingerprint, byte[] actualHostKeyBytes)
    {
        ArgumentNullException.ThrowIfNull(actualHostKeyBytes);
        var normalizedExpected = NormalizeHostKeyFingerprint(expectedFingerprint);
        var actualDigest = SHA256.HashData(actualHostKeyBytes);
        var actualBase64 = Convert.ToBase64String(actualDigest);

        var padded = normalizedExpected.PadRight((normalizedExpected.Length + 3) / 4 * 4, '=');
        var expectedBytes = Convert.FromBase64String(padded);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualDigest);
    }

    private static bool IsAllowedPathChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '=' or '+' or '@';
}
