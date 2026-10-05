using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Application.Backups;

public static class BackupInputValidation
{
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "PreCheckFailed", "SqlBackupFailed", "VerificationFailed", "HashFailed",
        "StorageWriteFailed", "ReplicaVerificationFailed", "InternalError", "Cancelled"
    };
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "Preparing", "BackingUp", "Verifying", "HashingSource", "PreparingArtifact", "HashingArtifact", "Uploading", "VerifyingReplica", "Completed", "Failed", "Cancelled"
    };

    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= 128 } && key.All(c => c is >= '!' and <= '~');
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Reference(string? value) => Text(value, 128) && value!.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool SafeKey(string? value) => value == string.Empty || value is { Length: <= 512 } && !value.StartsWith('/') && !value.Contains('\\') &&
        !value.Split('/').Any(s => s is "" or "." or "..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');

    public static bool IsValid(CreateBackupJobRequest request)
    {
        var c = request.Configuration;
        return Text(request.Name, 200) && request.AgentId != Guid.Empty && c.Engine == "mssql" && Text(c.Database, 128) &&
            Text(c.Host, 253) && c.Host.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or '\\' or ':') &&
            (c.Port is null or >= 1 and <= 65535) && c.StagingReference == "default" && c.StorageProvider is "local" or "central" &&
            SafeKey(c.StoragePrefix) && c.ChecksumPolicy is "Required" or "Preferred" or "Disabled" &&
            c.CompressionPolicy is "Required" or "Preferred" or "Disabled" &&
            (c.IntegratedSecurity ? c.DatabaseCredentialReference is null : Text(c.DatabaseCredentialReference, 64) &&
                c.DatabaseCredentialReference!.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'));
    }

    public static bool IsValidProgress(BackupTaskProgressRequest request) => request.LeaseToken != Guid.Empty && Stages.Contains(request.Stage) &&
        (request.Percent is null || double.IsFinite(request.Percent.Value) && request.Percent is >= 0 and <= 100);

    public static bool IsValidCompletion(BackupTaskCompletionRequest request, BackupConfiguration configuration)
    {
        if (request.LeaseToken == Guid.Empty) return false;
        if (request.Status == "Cancelled") return request.Artifact is null && request.FailureCode is null or "Cancelled";
        if (request.Status == "Failed") return request.Artifact is null && request.FailureCode is not null && Reasons.Contains(request.FailureCode) && request.FailureCode != "Cancelled";
        if (request.Status != "Succeeded" || request.FailureCode is not null || request.Artifact is not { } a) return false;
        if (a.SizeBytes <= 0 || a.CreatedAt == default || a.ChecksumAlgorithm != "sha256" || !Checksum(a.SourceChecksum) || !Checksum(a.ArtifactChecksum) ||
            a.Replicas is not { Count: > 0 and <= 8 } || !Metadata(a, configuration)) return false;
        return a.Replicas.All(r => r is not null && r.StorageProvider == configuration.StorageProvider && Reference(r.StorageTarget) &&
            Text(r.Key, 512) && SafeKey(r.Key) && r.SizeBytes == a.SizeBytes && r.ChecksumAlgorithm == a.ChecksumAlgorithm &&
            Checksum(r.Checksum) && r.Checksum.Equals(a.ArtifactChecksum, StringComparison.OrdinalIgnoreCase) && r.VerificationState == "Verified" &&
            r.VerificationMethod is "ReadBack" or "ServerSideChecksum" && r.CreatedAt != default && r.VerifiedAt is not null && r.VerifiedAt >= r.CreatedAt) &&
            a.Replicas.Select(r => (r.StorageProvider, r.StorageTarget, r.Key)).Distinct().Count() == a.Replicas.Count;
    }

    private static bool Checksum(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool Metadata(BackupTaskArtifact artifact, BackupConfiguration configuration)
    {
        if (artifact.SourceMetadataJson is not { Length: <= 16384 } || artifact.AppliedOptionsJson is not { Length: <= 4096 }) return false;
        try
        {
            using var source = JsonDocument.Parse(artifact.SourceMetadataJson, new JsonDocumentOptions { MaxDepth = 16 });
            using var options = JsonDocument.Parse(artifact.AppliedOptionsJson, new JsonDocumentOptions { MaxDepth = 16 });
            var root = source.RootElement;
            if (root.ValueKind != JsonValueKind.Object || options.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("engine", out var engine) || engine.GetString() != configuration.Engine ||
                !root.TryGetProperty("databaseName", out var database) || database.GetString() != configuration.Database ||
                !root.TryGetProperty("backupType", out var type) || type.GetString() != "Full" ||
                !root.TryGetProperty("backupStartedAt", out var start) || !start.TryGetDateTimeOffset(out var started) || started == default ||
                !root.TryGetProperty("backupFinishedAt", out var finish) || !finish.TryGetDateTimeOffset(out var finished) || finished < started || finished > artifact.CreatedAt ||
                !root.TryGetProperty("backupVerification", out var verification) || verification.ValueKind != JsonValueKind.Object ||
                !verification.TryGetProperty("state", out var state) || state.GetString() != "Passed" ||
                !verification.TryGetProperty("method", out var method) || !Text(method.GetString(), 64)) return false;
            return options.RootElement.TryGetProperty("copyOnly", out var copyOnly) && copyOnly.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                copyOnly.GetBoolean() == configuration.CopyOnly && options.RootElement.TryGetProperty("checksum", out var checksum) &&
                checksum.ValueKind is JsonValueKind.True or JsonValueKind.False && options.RootElement.TryGetProperty("compression", out var compression) &&
                compression.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                (configuration.ChecksumPolicy != "Required" || checksum.GetBoolean()) && (configuration.CompressionPolicy != "Required" || compression.GetBoolean()) &&
                (configuration.ChecksumPolicy != "Disabled" || !checksum.GetBoolean()) && (configuration.CompressionPolicy != "Disabled" || !compression.GetBoolean());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
