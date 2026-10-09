using System.Globalization;
using System.Net;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.Storage;

/// <summary>Validates the public connection definition without resolving DNS or opening a network connection.</summary>
internal static class SftpConnectionRules
{
    internal static readonly string[] LocationFields = ["host", "port", "username", "authenticationType", "remoteBaseDirectory"];

    public static void ValidateConfiguration(Dictionary<string, string> values)
    {
        values.TryAdd("port", "22");
        var host = values["host"];
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('%') || host.EndsWith('.'))
            Invalid("host");
        if (!int.TryParse(values["port"], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            Invalid("port");
        if (values["authenticationType"] is not ("password" or "private-key")) Invalid("authenticationType");
        var directory = values["remoteBaseDirectory"];
        if (!directory.StartsWith('/') || directory.Contains('\\') || directory.Contains("//", StringComparison.Ordinal)
            || directory.Split('/').Any(segment => segment is "." or "..")) Invalid("remoteBaseDirectory");
        if (directory.Length > 1) values["remoteBaseDirectory"] = directory.TrimEnd('/');
        var fingerprint = values["hostKeyFingerprint"];
        if (!fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)) Invalid("hostKeyFingerprint");
        var encoded = fingerprint[7..];
        Span<byte> decoded = stackalloc byte[32];
        var padded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        if (!Convert.TryFromBase64String(padded, decoded, out var length) || length != 32
            || Convert.ToBase64String(decoded).TrimEnd('=') != encoded.TrimEnd('=')) Invalid("hostKeyFingerprint");
        values["hostKeyFingerprint"] = "SHA256:" + encoded.TrimEnd('=');
    }

    public static void ValidateSecret(IReadOnlyDictionary<string, string> configuration, IReadOnlyDictionary<string, string> values)
    {
        var keyAuthentication = configuration["authenticationType"] == "private-key";
        var required = keyAuthentication ? "privateKey" : "password";
        if (!values.TryGetValue(required, out var value) || string.IsNullOrEmpty(value))
            throw new InvalidStorageConnectionException($"secret.{required}", "required");
        foreach (var name in values.Keys)
        {
            if (keyAuthentication ? name == "password" : name is "privateKey" or "passphrase")
                throw new InvalidStorageConnectionException($"secret.{name}", "authentication_type_mismatch");
        }
    }

    public static bool SameConfiguration(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public static StorageConnection Candidate(StorageConnection source, IReadOnlyDictionary<string, string> configuration, string? secretReference = null,
        IReadOnlyList<string>? fieldNames = null)
    {
        var candidate = new StorageConnection(source.Id, source.TenantId, source.Name, source.ProviderType, source.Ownership,
            source.QuotaScope, configuration, source.IsDefault, source.CreatedAt);
        if ((secretReference ?? source.SecretReference) is { } reference)
            candidate.AttachSecret(reference, fieldNames ?? source.SecretFieldNames, source.SecretUpdatedAt ?? source.CreatedAt);
        return candidate;
    }

    private static void Invalid(string field) => throw new InvalidStorageConnectionException($"configuration.{field}", "invalid_value");
}
