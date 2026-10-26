using BackupPlatform.Domain.Storage;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Storage;

internal static class NetworkStorageConnectionRules
{
    public static void Validate(Dictionary<string, string> configuration, string provider)
    {
        if (configuration.TryGetValue("site", out var site) && (site.Length == 0 || site.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
            throw new InvalidStorageConnectionException("configuration.site", "invalid_value");
        if (configuration.TryGetValue("mediaClass", out var media) && media is not ("disk" or "object-storage" or "tape"))
            throw new InvalidStorageConnectionException("configuration.mediaClass", "invalid_value");
        try
        {
            if (provider == StorageProviderTypes.S3)
            {
                if (!Uri.TryCreate(configuration.GetValueOrDefault("endpoint"), UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0
                    || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/" || Uri.CheckHostName(endpoint.Host) == UriHostNameType.Unknown)
                    throw new ArgumentException();
                var bucket = configuration.GetValueOrDefault("bucket") ?? "";
                if (bucket.Length is < 3 or > 63 || bucket.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
                    || !char.IsAsciiLetterOrDigit(bucket[0]) || !char.IsAsciiLetterOrDigit(bucket[^1]) || bucket.Contains(".."))
                    throw new ArgumentException();
                var region = configuration.GetValueOrDefault("region") ?? "";
                if (region.Length is < 1 or > 64 || region.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                    throw new ArgumentException();
                var mode = configuration.GetValueOrDefault("objectLockMode");
                var days = configuration.TryGetValue("retentionDays", out var value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                if (mode is not (null or "COMPLIANCE" or "GOVERNANCE") || (mode is null ? days != 0 : days is < 1 or > 36500))
                    throw new ArgumentException();
            }
            else if (provider == StorageProviderTypes.Smb)
            {
                var host = configuration.GetValueOrDefault("host") ?? "";
                var share = configuration.GetValueOrDefault("share") ?? "";
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('%') || host.Contains(':')
                    || share.Length is < 1 or > 80 || share.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '$')))
                    throw new ArgumentException();
            }
        }
        catch (Exception error) when (error is StorageException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidStorageConnectionException("configuration", "invalid_value");
        }
    }
}
