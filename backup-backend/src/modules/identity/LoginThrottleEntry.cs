namespace BackupPlatform.Identity;

/// <summary>Failed-login counter of one login identifier (trimmed, upper-cased) within its current window; shared by every API instance through the database.</summary>
public sealed class LoginThrottleEntry
{
    private LoginThrottleEntry() { Identifier = null!; }

    public string Identifier { get; private set; }
    public int Failures { get; private set; }
    public DateTimeOffset WindowStart { get; private set; }
}
