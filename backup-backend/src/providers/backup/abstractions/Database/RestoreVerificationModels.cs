using System.Text.RegularExpressions;

namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>
/// Names of the databases the platform itself creates and may later remove. Both are 128-bit random, so a name is a proof of origin: the platform never
/// drops a database whose name does not match, and a restore never creates a user-chosen database with a reserved prefix.
/// </summary>
public static partial class RestoreDatabaseNames
{
    /// <summary>Prefix of the disposable database an isolated restore verification creates.</summary>
    public const string VerificationPrefix = "bp_verify_";

    /// <summary>Prefix of the rollback copy PostgreSQL keeps when a restore replaces an existing database. Never removed by the platform on its own.</summary>
    public const string RollbackPrefix = "bp_old_";

    public static string NewVerificationName() => VerificationPrefix + Guid.NewGuid().ToString("N");

    public static bool IsVerificationName(string? name) => name is not null && VerificationPattern().IsMatch(name);

    public static bool IsRollbackName(string? name) => name is not null && RollbackPattern().IsMatch(name);

    /// <summary>True for any name a user-requested restore must not target: the reserved prefixes are the platform's own.</summary>
    public static bool IsReserved(string? name) => name is not null &&
        (name.StartsWith(VerificationPrefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(RollbackPrefix, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^bp_verify_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex VerificationPattern();

    [GeneratedRegex("^bp_old_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RollbackPattern();
}

/// <summary>One verification check of a restored database. <see cref="ReasonCode"/> is stable and machine-readable; <see cref="Message"/> is a fixed user-safe sentence.</summary>
public sealed record RestoreVerificationCheck(string CheckId, bool Passed, string ReasonCode, string Message)
{
    public const string DatabaseOnline = "database-online";
    public const string Integrity = "integrity";
    public const string DataReadable = "data-readable";
}

/// <summary>
/// What the engine established about a restored, disposable database. <see cref="Checks"/> always lists every check that ran (a failed check stops later
/// ones that depend on it). <see cref="UserTableCount"/> and <see cref="ReadableTableCount"/> describe the data read test; null when it did not run.
/// </summary>
public sealed record RestoreVerificationReport(IReadOnlyList<RestoreVerificationCheck> Checks, int? UserTableCount = null, int? ReadableTableCount = null)
{
    public bool Passed => Checks.Count > 0 && Checks.All(check => check.Passed);
}

public sealed record RestoreVerificationRequest
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    public RestoreVerificationRequest(string databaseName, TimeSpan? timeout = null)
    {
        if (!RestoreDatabaseNames.IsVerificationName(databaseName))
        {
            throw new ArgumentException("Only a platform verification database can be verified.", nameof(databaseName));
        }

        var value = timeout ?? DefaultTimeout;
        if (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), value, "Timeouts must be between 1 second and 7 days.");
        }

        DatabaseName = databaseName;
        Timeout = value;
    }

    public string DatabaseName { get; }
    public TimeSpan Timeout { get; }
}

/// <summary>What a platform-owned database is: a disposable verification database or the rollback copy of a replaced database.</summary>
public enum DatabaseDropKind
{
    VerificationDatabase = 1,
    RollbackCopy = 2
}

public enum DatabaseDropOutcome
{
    /// <summary>The database existed and is gone.</summary>
    Dropped = 1,
    /// <summary>No database of that name existed: the goal (it does not exist) already holds, so a retried or repeated drop is a success.</summary>
    NotFound,
    /// <summary>The database may still exist; <see cref="DatabaseDropResult.ReasonCode"/> says why.</summary>
    Failed
}

/// <summary>Removes one database the platform created. The name must match the kind's reserved pattern; the provider refuses anything else.</summary>
public sealed record DatabaseDropRequest
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public DatabaseDropRequest(string databaseName, DatabaseDropKind kind, TimeSpan? timeout = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown database kind.");
        }

        var owned = kind == DatabaseDropKind.VerificationDatabase
            ? RestoreDatabaseNames.IsVerificationName(databaseName)
            : RestoreDatabaseNames.IsRollbackName(databaseName);
        if (!owned)
        {
            throw new ArgumentException("Only a database the platform created can be dropped.", nameof(databaseName));
        }

        var value = timeout ?? DefaultTimeout;
        if (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromHours(12))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), value, "Timeouts must be between 1 second and 12 hours.");
        }

        DatabaseName = databaseName;
        Kind = kind;
        Timeout = value;
    }

    public string DatabaseName { get; }
    public DatabaseDropKind Kind { get; }
    public TimeSpan Timeout { get; }
}

/// <summary><see cref="ReasonCode"/> is set exactly when <see cref="Outcome"/> is <see cref="DatabaseDropOutcome.Failed"/>; it is a stable machine code, never engine text.</summary>
public sealed record DatabaseDropResult(DatabaseDropOutcome Outcome, string? ReasonCode = null)
{
    public bool Removed => Outcome is DatabaseDropOutcome.Dropped or DatabaseDropOutcome.NotFound;
}

/// <summary>
/// Optional capability of an engine's restore provider: proving a restored database is usable and removing databases the platform created.
/// Both operations are idempotent and never touch a database whose name is not one the platform generated. Expected failures are results
/// (a failed check, <see cref="DatabaseDropOutcome.Failed"/>); caller cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IRestoreVerificationProvider
{
    /// <summary>Opens the verification database, runs the engine's integrity check and reads every user table. Changes nothing.</summary>
    Task<RestoreVerificationReport> VerifyDatabaseAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestoreVerificationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Drops the named platform database (ending sessions the platform itself could have left) or reports that none exists.</summary>
    Task<DatabaseDropResult> DropDatabaseAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseDropRequest request,
        CancellationToken cancellationToken);
}
