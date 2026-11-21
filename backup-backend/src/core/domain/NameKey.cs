namespace BackupPlatform.Domain;

/// <summary>
/// The case-insensitive uniqueness key of a display name, stored beside it. A check constraint requires it to equal Postgres <c>lower(btrim(name))</c>, so it must
/// lower-case the way Postgres does: .NET's invariant rules leave <c>İ</c> (U+0130) alone, Postgres turns it into <c>i</c>, and a name like "İstanbul" failed the
/// constraint (HTTP 500).
/// </summary>
public static class NameKey
{
    public static string Of(string trimmedName) => trimmedName.Replace('İ', 'i').ToLowerInvariant();
}
