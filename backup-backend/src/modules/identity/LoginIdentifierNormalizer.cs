namespace BackupPlatform.Identity;

public static class LoginIdentifierNormalizer
{
    public static string Normalize(string loginIdentifier)
    {
        ArgumentNullException.ThrowIfNull(loginIdentifier);
        var trimmed = loginIdentifier.Trim();
        if (trimmed.Length is < 3 or > 128)
        {
            throw new ArgumentException("A login identifier must contain 3 to 128 ASCII username characters.", nameof(loginIdentifier));
        }
        foreach (var character in trimmed)
        {
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                throw new ArgumentException("A login identifier contains a forbidden character.", nameof(loginIdentifier));
            }
        }
        return trimmed.ToUpperInvariant();
    }
}
