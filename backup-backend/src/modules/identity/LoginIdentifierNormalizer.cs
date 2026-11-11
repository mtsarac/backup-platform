using System.Diagnostics.CodeAnalysis;

namespace BackupPlatform.Identity;

public static class LoginIdentifierNormalizer
{
    public static string Normalize(string loginIdentifier)
    {
        ArgumentNullException.ThrowIfNull(loginIdentifier);
        return TryNormalize(loginIdentifier, out var normalized)
            ? normalized
            : throw new ArgumentException("A login identifier must contain 3 to 128 ASCII username characters (letters, digits, '.', '_', '-').", nameof(loginIdentifier));
    }

    public static bool TryNormalize(string? loginIdentifier, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        var trimmed = loginIdentifier?.Trim();
        if (trimmed is not { Length: >= 3 and <= 128 })
        {
            return false;
        }
        foreach (var character in trimmed)
        {
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                return false;
            }
        }
        normalized = trimmed.ToUpperInvariant();
        return true;
    }

    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var trimmed = email.Trim();
        if (trimmed.Length > 254 || trimmed.Any(c => c > 127) ||
            !System.Net.Mail.MailAddress.TryCreate(trimmed, out var address) || address.Address != trimmed)
            throw new ArgumentException("A valid email address is required.", nameof(email));
        return trimmed.ToUpperInvariant();
    }
}
