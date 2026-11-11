namespace BackupPlatform.Identity;

public sealed class LoginThrottleEntry
{
    private LoginThrottleEntry() { Kind = null!; Scope = null!; Identifier = null!; }

    public string Kind { get; private set; }
    public string Scope { get; private set; }
    public string Identifier { get; private set; }
    public int Failures { get; private set; }
    public DateTimeOffset LastFailureAt { get; private set; }
    public DateTimeOffset LockedUntil { get; private set; }
}
