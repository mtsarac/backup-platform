namespace BackupPlatform.Contracts.Agents;

/// <summary>
/// Agent API authentication: <c>Authorization: BackupAgent &lt;credential-id&gt;.&lt;secret&gt;</c> over TLS only.
/// The secret travels only in this header, never in a query string, cookie, or custom header.
/// </summary>
public static class AgentAuthorizationScheme
{
    public const string Name = "BackupAgent";

    public static string FormatHeaderValue(Guid credentialId, string secret) =>
        $"{Name} {AgentSecretReference.Format(credentialId, secret)}";

    /// <summary>Extracts a well-formed credential from an Authorization header value; malformed values never throw.</summary>
    public static bool TryParseHeaderValue(string? headerValue, out Guid credentialId, out string secret)
    {
        credentialId = Guid.Empty;
        secret = string.Empty;
        const int prefixLength = 11 + 1; // "BackupAgent" + one space
        if (headerValue is null
            || headerValue.Length != prefixLength + AgentSecretReference.ReferenceLength
            || !headerValue.StartsWith(Name, StringComparison.OrdinalIgnoreCase)
            || headerValue[Name.Length] != ' ')
        {
            return false;
        }
        return AgentSecretReference.TryParse(headerValue[prefixLength..], out credentialId, out secret);
    }
}
