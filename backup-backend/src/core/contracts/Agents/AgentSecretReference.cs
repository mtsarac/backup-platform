using System.Globalization;

namespace BackupPlatform.Contracts.Agents;

/// <summary>
/// Wire grammar shared by enrollment tokens and agent credentials: <c>&lt;32 hex public id&gt;.&lt;43 base64url secret&gt;</c>.
/// The public identifier selects a stored record; only the secret authenticates and is never stored by the backend.
/// </summary>
public static class AgentSecretReference
{
    public const int SecretLength = 43;
    public const int ReferenceLength = 32 + 1 + SecretLength;

    public static string Format(Guid publicId, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (publicId == Guid.Empty || !IsWellFormedSecret(secret))
        {
            throw new ArgumentException("The public identifier and secret must be well formed.");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{publicId:N}.{secret}");
    }

    /// <summary>Strictly parses a reference without allocating diagnostics that could echo the secret.</summary>
    public static bool TryParse(string? value, out Guid publicId, out string secret)
    {
        publicId = Guid.Empty;
        secret = string.Empty;
        if (value is null || value.Length != ReferenceLength || value[32] != '.')
        {
            return false;
        }
        if (!Guid.TryParseExact(value.AsSpan(0, 32), "N", out publicId) || publicId == Guid.Empty)
        {
            publicId = Guid.Empty;
            return false;
        }
        var candidate = value[33..];
        if (!IsWellFormedSecret(candidate))
        {
            publicId = Guid.Empty;
            return false;
        }
        secret = candidate;
        return true;
    }

    public static bool IsWellFormedSecret(string secret)
    {
        if (secret.Length != SecretLength)
        {
            return false;
        }
        foreach (var character in secret)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }
        return true;
    }
}
