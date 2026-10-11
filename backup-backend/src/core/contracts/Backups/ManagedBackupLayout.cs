using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BackupPlatform.Contracts.Backups;

/// <summary>A storage key that the platform itself produced: <c>[prefix/]tenant/job/run/attempt/file</c>.</summary>
/// <param name="Prefix">The job's configured storage prefix, or empty.</param>
/// <param name="FileName">The artifact name, or the hidden <c>.partial</c> temporary name of an upload that has not been published.</param>
/// <param name="IsPartial">True for an unpublished temporary upload file.</param>
public sealed record ManagedStorageKey(string Prefix, Guid TenantId, Guid JobId, Guid RunId, Guid AttemptId, string FileName, bool IsPartial);

/// <summary>
/// The one definition of what the platform's own files look like, shared by whoever writes them (the agent) and whoever may
/// later clean them up (the backend, the agent's staging sweeper). Cleanup recognises a file ONLY by this grammar: a file whose
/// name or location does not match is somebody else's and is never touched.
/// <list type="bullet">
/// <item><description>Staged and stored artifact: <c>{database}_{yyyyMMddTHHmmssZ}_{8 hex}_FULL.bak</c>.</description></item>
/// <item><description>Storage key: <c>[{prefix}/]{tenant:N}/{job:N}/{run:N}/{attempt:N}/{artifact name}</c>. The four identifiers are what lets the
/// backend prove which attempt a stored file belongs to.</description></item>
/// <item><description>Unpublished upload: <c>.{artifact name}.{32 hex}.partial</c>, or SFTP's deterministic <c>.{artifact name}.partial</c>, in the same directory.</description></item>
/// </list>
/// </summary>
public static class ManagedBackupLayout
{
    private const int MaxDatabaseNameLength = 48;
    private const string ArtifactPattern = @"[A-Za-z0-9_\-]{1,48}_[0-9]{8}T[0-9]{6}Z_[0-9a-f]{8}_FULL\.bak";

    // \z, not $: in .NET `$` also matches before a trailing newline, and a name with a newline is not a managed name.
    private static readonly Regex ArtifactName = new($@"^{ArtifactPattern}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PartialName = new($@"^\.{ArtifactPattern}(?:\.[0-9a-f]{{32}})?\.partial\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The staged file name. It is derived from untrusted input (the database name), so it is reduced to a safe, portable alphabet;
    /// uniqueness comes from the timestamp plus a random part, never from the database name, because no staged file may ever be
    /// appended to or silently overwritten.
    /// </summary>
    public static string StagedFileName(string databaseName, DateTimeOffset startedAt, Guid unique)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        var safe = new StringBuilder(Math.Min(databaseName.Length, MaxDatabaseNameLength));
        foreach (var character in databaseName)
        {
            if (safe.Length == MaxDatabaseNameLength)
            {
                break;
            }

            safe.Append(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? character : '_');
        }

        if (safe.Length == 0)
        {
            safe.Append("database");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{safe}_{startedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{unique.ToString("N", CultureInfo.InvariantCulture)[..8]}_FULL.bak");
    }

    /// <summary>The key segments that identify one attempt: <c>tenant/job/run/attempt</c>, each in 32-digit lowercase form.</summary>
    public static string AttemptScope(Guid tenantId, Guid jobId, Guid runId, Guid attemptId) =>
        string.Create(CultureInfo.InvariantCulture, $"{tenantId:N}/{jobId:N}/{runId:N}/{attemptId:N}");

    /// <summary>True when <paramref name="fileName"/> is exactly a name <see cref="StagedFileName"/> can produce.</summary>
    public static bool IsManagedArtifactName(string? fileName) => fileName is not null && fileName.Length <= 128 && ArtifactName.IsMatch(fileName);

    /// <summary>True when <paramref name="fileName"/> is the temporary name a storage engine gives an unpublished upload of a managed artifact.</summary>
    public static bool IsManagedPartialName(string? fileName) => fileName is not null && fileName.Length <= 192 && PartialName.IsMatch(fileName);

    /// <summary>
    /// Recognises a storage key of the managed layout. Anything else (too few segments, identifiers that are not 32-digit lowercase
    /// GUIDs, a file name outside the grammar) is not ours and yields false.
    /// </summary>
    public static bool TryParseStorageKey(string? key, out ManagedStorageKey? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(key) || key.Length > 1024)
        {
            return false;
        }

        var segments = key.Split('/');
        if (segments.Length < 5)
        {
            return false;
        }

        var fileName = segments[^1];
        var partial = IsManagedPartialName(fileName);
        if (!partial && !IsManagedArtifactName(fileName))
        {
            return false;
        }

        Span<Guid> ids = stackalloc Guid[4];
        for (var index = 0; index < 4; index++)
        {
            var text = segments[segments.Length - 5 + index];
            if (text.Length != 32 || text.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                || !Guid.TryParseExact(text, "N", out ids[index]) || ids[index] == Guid.Empty)
            {
                return false;
            }
        }

        parsed = new ManagedStorageKey(string.Join('/', segments.Take(segments.Length - 5)), ids[0], ids[1], ids[2], ids[3], fileName, partial);
        return true;
    }
}
