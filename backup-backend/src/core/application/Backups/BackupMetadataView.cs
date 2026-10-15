using System.Text.Json;

namespace BackupPlatform.Application.Backups;

public sealed record BackupMetadataView(string? Engine, string? DatabaseName, string? BackupType, DateTimeOffset? BackupStartedAt, DateTimeOffset? BackupFinishedAt,
    BackupInstanceMetadataView? Instance, BackupSqlDetailsView? SqlDetails, BackupVerificationView? Verification, BackupAppliedOptionsView? AppliedOptions,
    BackupPostgreSqlDetailsView? PostgreSqlDetails = null);
public sealed record BackupInstanceMetadataView(string? ServerName, string? InstanceName, string? Version, string? Edition, string? HostPlatform);
public sealed record BackupSqlDetailsView(string? RecoveryModel, int? CompatibilityLevel, string? TransparentDataEncryption, string? UserAccess);
public sealed record BackupVerificationView(string? State, string? Method, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
public sealed record BackupAppliedOptionsView(bool? CopyOnly, bool? Checksum, bool? Compression);
public sealed record BackupPostgreSqlDetailsView(string? ArchiveFormat, string? ClientVersion, string? Encoding, string? Collation, string? DataChecksums, string? DatabaseSizeBytes, string? Extensions);

/// <summary>Projects only known backup facts. Unknown JSON keys and arbitrary provider detail bags never cross the browser API.</summary>
public static class BackupMetadataProjection
{
    public static BackupMetadataView? Read(string sourceJson, string optionsJson)
    {
        using var source = JsonDocument.Parse(sourceJson);
        using var options = JsonDocument.Parse(optionsJson);
        var root = source.RootElement;
        var applied = options.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("engine", out _)) return null;
        var instance = Object(root, "instance");
        var details = Object(root, "details");
        var verification = Object(root, "backupVerification");
        return new(Text(root, "engine"), Text(root, "databaseName"), Text(root, "backupType"), Timestamp(root, "backupStartedAt"), Timestamp(root, "backupFinishedAt"),
            instance is { } i ? new(Text(i, "serverName"), Text(i, "instanceName"), Text(i, "version"), Text(i, "edition"), Text(i, "hostPlatform")) : null,
            details is { } d ? new(Text(d, "recovery-model"), Integer(d, "compatibility-level"), Text(d, "transparent-data-encryption"), Text(d, "user-access")) : null,
            verification is { } v ? new(Text(v, "state"), Text(v, "method"), Timestamp(v, "startedAt"), Timestamp(v, "finishedAt")) : null,
            applied.ValueKind == JsonValueKind.Object ? new(Boolean(applied, "copyOnly"), Boolean(applied, "checksum"), Boolean(applied, "compression")) : null,
            Text(root, "engine") == "postgres" && details is { } pg
                ? new(Text(pg, "archive-format"), Text(pg, "client-version"), Text(pg, "encoding"), Text(pg, "collation"), Text(pg, "data-checksums"),
                    Text(pg, "database-size-bytes"), Text(pg, "extensions")) : null);
    }

    private static JsonElement? Object(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: <= 256 } text && !text.Any(char.IsControl) ? text : null;
    private static DateTimeOffset? Timestamp(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
    private static bool? Boolean(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static int? Integer(JsonElement root, string name) => int.TryParse(Text(root, name), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
}
