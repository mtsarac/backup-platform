namespace BackupPlatform.Identity;

/// <summary>
/// Enforces password policy:
/// - Minimum length: 12 characters.
/// - Maximum length: 1024 characters.
/// - No composition rules (lowercase-only is valid).
/// - Rejects common, predictable and breached passwords without requiring external network calls.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;
    public const int MaximumLength = 1024;

    private static readonly HashSet<string> BlockedPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234",
        "password12345",
        "123456789012",
        "1234567890123",
        "qwertyuiop12",
        "qwertyuiopas",
        "admin1234567",
        "administrator",
        "welcome12345",
        "letmein12345",
        "backupplatform",
        "correcthorsebatterystaple",
        "changeme1234",
        "iloveyou1234",
        "monkey123456",
        "dragon123456",
        "trustnoone12",
        "master123456",
        "sunshine1234",
        "princess1234",
        "football1234",
        "baseball1234",
        "charlie12345",
        "shadow123456",
        "superman1234",
        "jordan123456",
        "harley123456",
        "mustang12345",
        "killer123456",
        "chelsea12345",
        "liverpool123",
        "arsenal12345",
        "barcelona123",
        "realmadrid12",
        "starwars1234",
        "pokemon12345",
        "whatever1234",
        "freedom12345",
        "fuckyou12345",
        "secret123456",
        "system123456",
        "root12345678",
        "default12345",
        "testing12345",
        "database1234",
        "computer1234",
        "internet1234",
        "security1234",
        "network12345",
        "support12345",
        "company12345"
    };

    public static bool Validate(string? password, out string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            failureReason = "password_required";
            return false;
        }

        if (password.Length < MinimumLength)
        {
            failureReason = "password_too_short";
            return false;
        }

        if (password.Length > MaximumLength)
        {
            failureReason = "password_too_long";
            return false;
        }

        if (BlockedPasswords.Contains(password.Trim()))
        {
            failureReason = "password_common_or_breached";
            return false;
        }

        failureReason = null;
        return true;
    }

    public static void ThrowIfInvalid(string? password)
    {
        if (!Validate(password, out var reason))
        {
            throw new ArgumentException(reason switch
            {
                "password_too_short" => "A password must be at least 12 characters long.",
                "password_too_long" => "A password must not exceed 1024 characters.",
                "password_common_or_breached" => "This password is too common or known to be breached. Choose a different password.",
                _ => "A valid password is required."
            }, nameof(password));
        }
    }
}
