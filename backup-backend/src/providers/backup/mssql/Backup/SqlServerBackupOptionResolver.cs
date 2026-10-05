using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;

namespace BackupPlatform.BackupProviders.MSSQL.Backup;

/// <summary>Machine-readable codes of the warnings option resolution can raise.</summary>
public static class SqlServerBackupWarningCodes
{
    public const string CopyOnlyUnsupportedFallback = "copy-only-unsupported-fallback";
    public const string CopyOnlySupportUnknownSkipped = "copy-only-support-unknown-skipped";
    public const string CopyOnlySupportUnknownApplied = "copy-only-support-unknown-applied";
    public const string ChecksumUnsupportedFallback = "checksum-unsupported-fallback";
    public const string ChecksumSupportUnknownSkipped = "checksum-support-unknown-skipped";
    public const string ChecksumSupportUnknownApplied = "checksum-support-unknown-applied";
    public const string CompressionUnsupportedFallback = "compression-unsupported-fallback";
    public const string CompressionSupportUnknownSkipped = "compression-support-unknown-skipped";
    public const string CompressionSupportUnknownApplied = "compression-support-unknown-applied";
    public const string StagingCleanupFailed = "staging-cleanup-failed";
    public const string ServerFileMayRemain = "server-file-may-remain";
    public const string VerifyPermissionUnknown = "verify-permission-unknown";
}

/// <summary>What option resolution decided: the requested/applied pair, the warnings that explain every difference, and any required option the engine cannot provide.</summary>
internal sealed record SqlServerOptionResolution(
    BackupOptionsOutcome Outcome,
    IReadOnlyList<BackupWarning> Warnings,
    IReadOnlyList<string> UnsupportedRequired,
    bool CompressionCapabilitySupported);

/// <summary>
/// Decides, from the requested mode and the capability the engine reports, whether each option is applied. Pure. The policy is
/// the one documented on <see cref="BackupOptionMode"/>: an option is never used silently and never skipped silently. An
/// Unsupported + Required option is not applied here and is reported in <see cref="SqlServerOptionResolution.UnsupportedRequired"/>;
/// the pre-check rejects that case earlier, so execution treats it as a failed precondition and never starts the backup.
/// </summary>
internal static class SqlServerBackupOptionResolver
{
    public static SqlServerOptionResolution Resolve(FullBackupOptions requested, DatabaseCapabilitySet capabilities)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(capabilities);

        var warnings = new List<BackupWarning>();
        var unsupported = new List<string>();

        var copyOnly = ResolveOne("copy-only", requested.CopyOnly, capabilities.SupportOf(SqlServerCapabilityIds.CopyOnly), warnings, unsupported,
            SqlServerBackupWarningCodes.CopyOnlyUnsupportedFallback,
            SqlServerBackupWarningCodes.CopyOnlySupportUnknownSkipped,
            SqlServerBackupWarningCodes.CopyOnlySupportUnknownApplied,
            "COPY_ONLY");
        var checksum = ResolveOne("checksum", requested.Checksum, capabilities.SupportOf(SqlServerCapabilityIds.BackupChecksum), warnings, unsupported,
            SqlServerBackupWarningCodes.ChecksumUnsupportedFallback,
            SqlServerBackupWarningCodes.ChecksumSupportUnknownSkipped,
            SqlServerBackupWarningCodes.ChecksumSupportUnknownApplied,
            "backup checksums");
        var compressionSupport = capabilities.SupportOf(SqlServerCapabilityIds.BackupCompression);
        var compression = ResolveOne("compression", requested.Compression, compressionSupport, warnings, unsupported,
            SqlServerBackupWarningCodes.CompressionUnsupportedFallback,
            SqlServerBackupWarningCodes.CompressionSupportUnknownSkipped,
            SqlServerBackupWarningCodes.CompressionSupportUnknownApplied,
            "backup compression");

        return new SqlServerOptionResolution(
            new BackupOptionsOutcome(requested, new AppliedFullBackupOptions(copyOnly, checksum, compression)),
            warnings,
            unsupported,
            compressionSupport == DatabaseCapabilitySupport.Supported);
    }

    private static bool ResolveOne(
        string key,
        BackupOptionMode mode,
        DatabaseCapabilitySupport support,
        List<BackupWarning> warnings,
        List<string> unsupported,
        string fallbackCode,
        string skippedCode,
        string appliedCode,
        string label)
    {
        if (mode == BackupOptionMode.Off)
        {
            return false;
        }

        switch (support)
        {
            case DatabaseCapabilitySupport.Supported:
                return true;
            case DatabaseCapabilitySupport.Unsupported when mode == BackupOptionMode.Required:
                unsupported.Add(key);
                return false;
            case DatabaseCapabilitySupport.Unsupported:
                warnings.Add(new BackupWarning(fallbackCode, $"{label} is not supported here; the backup was taken without it."));
                return false;
            case DatabaseCapabilitySupport.Unknown when mode == BackupOptionMode.Required:
                warnings.Add(new BackupWarning(appliedCode, $"Support for {label} could not be determined; it was requested as required, so the server decides."));
                return true;
            default:
                warnings.Add(new BackupWarning(skippedCode, $"Support for {label} could not be determined; the backup was taken without it."));
                return false;
        }
    }
}
