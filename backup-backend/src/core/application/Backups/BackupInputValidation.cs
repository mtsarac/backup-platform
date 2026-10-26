using BackupPlatform.Domain.Storage;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Application.Backups;

public sealed class InvalidBackupJobException(string field, string code = "invalid_value") : ArgumentException("The backup job field is invalid.")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

public static class BackupInputValidation
{
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "Preparing", "BackingUp", "Verifying", "HashingSource", "PreparingArtifact", "HashingArtifact", "Uploading", "VerifyingReplica", "Completed", "Failed", "Cancelled"
    };

    /// <summary>Manual keys may not use the scheduler's reserved <c>scheduled-</c> prefix: a manual run could otherwise occupy a future occurrence's unique key.</summary>
    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= 128 } && key.All(c => c is >= '!' and <= '~') && !key.StartsWith("scheduled-", StringComparison.OrdinalIgnoreCase);
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Reference(string? value) => Text(value, 128) && value!.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool SafeKey(string? value) => value == string.Empty || value is { Length: <= StorageObjectKey.MaxLength } && !value.StartsWith('/') && !value.Contains('\\') &&
        !value.Split('/').Any(s => s is "" or "." or "..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');

    /// <summary>Safe field-level errors; never exposes configuration values or parser exception messages.</summary>
    public static void Validate(CreateBackupJobRequest request)
    {
        var c = request.Configuration;
        Check(Text(request.Name, 200), "name");
        Check(request.AgentId != Guid.Empty, "agentId");
        Check(c is not null, "configuration");
        Check(DatabaseEngines.IsValid(c!.Engine), "configuration.engine");
        Check(Text(c.Database, 128), "configuration.database");
        Check(c.Engine != "postgres" || c.Database == c.Database.Trim() && System.Text.Encoding.UTF8.GetByteCount(c.Database) <= 63, "configuration.database");
        Check(c.Engine != "postgres" || !c.IntegratedSecurity, "configuration.integratedSecurity");
        if (c.Engine == DatabaseEngines.FileSystem)
        {
            Check(Reference(c.Database), "configuration.database");
            Check(c.Host == "localhost" && c.Port is null && c.IntegratedSecurity && c.DatabaseCredentialReference is null && c.DatabaseLogin is null, "configuration.host");
            Check(c.Files is not null, "configuration.files");
            try { new FileBackupPolicy { Paths = c.Files!.Paths, Includes = c.Files.Includes, Excludes = c.Files.Excludes, SymlinkPolicy = c.Files.SymlinkPolicy }.Validate(); }
            catch (ArgumentException) { throw new InvalidBackupJobException("configuration.files"); }
        }
        else Check(c.Files is null, "configuration.files");
        Check(Text(c.Host, 253) && c.Host.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or '\\' or ':'), "configuration.host");
        Check(c.Port is null or >= 1 and <= 65535, "configuration.port");
        Check(c.StagingReference == "default", "configuration.stagingReference");
        Check(c.StoragePrefix is { Length: <= 512 } && SafeKey(c.StoragePrefix), "configuration.storagePrefix");
        Check(c.ChecksumPolicy is "Required" or "Preferred" or "Disabled", "configuration.checksumPolicy");
        Check(c.CompressionPolicy is "Required" or "Preferred" or "Disabled", "configuration.compressionPolicy");
        Check(c.EncryptionPolicy is "Off" or "Optional" or "Required", "configuration.encryptionPolicy");
        Check(c.IntegratedSecurity ? c.DatabaseCredentialReference is null : Text(c.DatabaseCredentialReference, 64) && c.DatabaseCredentialReference!.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'), "configuration.databaseCredentialReference");

        var destinations = c.Destinations ?? [];
        Check(destinations.Count is > 0 and <= MaxDestinations, "configuration.destinations");
        Check(destinations.Count(d => d.IsPrimary) == 1, "configuration.destinations", "primary_destination_required");
        Check(destinations.Select(d => (d.StorageProvider, d.StorageConnectionId)).Distinct().Count() == destinations.Count, "configuration.destinations", "duplicate_destination");

        for (var i = 0; i < destinations.Count; i++)
        {
            var d = destinations[i];
            var prefix = $"configuration.destinations[{i}].";
            Check(d.StorageProvider is StorageProviderTypes.Local or StorageProviderTypes.Central || StorageProviderTypes.External(d.StorageProvider), $"{prefix}storageProvider");
            Check(d.StorageExecutionLocation is "Backend" or "Agent" && (d.StorageExecutionLocation != "Agent" || d.StorageProvider == StorageProviderTypes.Local), $"{prefix}storageExecutionLocation");
            Check(!StorageProviderTypes.External(d.StorageProvider) || c.StoragePrefix == string.Empty, "configuration.storagePrefix");
            Check(!StorageProviderTypes.External(d.StorageProvider) || d.StorageConnectionId is { } connection && connection != Guid.Empty, $"{prefix}storageConnectionId");
        }

        Check(c.RequiredVerifiedReplicaCount >= 1 && c.RequiredVerifiedReplicaCount <= destinations.Count, "configuration.requiredVerifiedReplicaCount");
        Check(!c.EnforceThreeTwoOne || c.RequiredVerifiedReplicaCount >= 3 && c.SourceSite is { Length: > 0 and <= 64 }
            && c.SourceSite.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'), "configuration.sourceSite");
        if (request.RetryPolicy is { } retry)
        {
            Check(retry.MaxAttempts is null or >= 1 and <= BackupRetryPolicy.MaximumAttemptsLimit, "retryPolicy.maxAttempts");
            var initial = retry.InitialDelaySeconds ?? BackupRetryPolicy.Default.InitialDelay.TotalSeconds;
            var maximum = retry.MaxDelaySeconds ?? BackupRetryPolicy.Default.MaxDelay.TotalSeconds;
            Check(double.IsFinite(initial) && initial is >= 1 and <= 86_400, "retryPolicy.initialDelaySeconds");
            Check(double.IsFinite(maximum) && maximum >= initial && maximum <= 86_400, "retryPolicy.maxDelaySeconds");
            Check(retry.Jitter is null || double.IsFinite(retry.Jitter.Value) && retry.Jitter is >= 0 and <= 1, "retryPolicy.jitter");
            Check(retry.Backoff is null || retry.Backoff.Equals("Fixed", StringComparison.OrdinalIgnoreCase) || retry.Backoff.Equals("Exponential", StringComparison.OrdinalIgnoreCase), "retryPolicy.backoff");
        }
        try { _ = BackupOrchestrator.ToDomain(request.OverlapPolicy); }
        catch (ArgumentException) { throw new InvalidBackupJobException("overlapPolicy"); }
        if (request.Schedule?.OverlapPolicy is { } scheduled && request.OverlapPolicy is { } overlap)
            Check(string.Equals(scheduled, overlap, StringComparison.OrdinalIgnoreCase), "overlapPolicy", "conflicting_policy");
    }

    private static void Check(bool valid, string field, string code = "invalid_value")
    {
        if (!valid) throw new InvalidBackupJobException(field, code);
    }

    public static bool IsValidProgress(BackupTaskProgressRequest request) => request.LeaseToken != Guid.Empty && Stages.Contains(request.Stage) &&
        (request.Percent is null || double.IsFinite(request.Percent.Value) && request.Percent is >= 0 and <= 100) &&
        (request.Checkpoint is not { } checkpoint || checkpoint.DestinationId != Guid.Empty && checkpoint.VerifiedByteOffset >= 0 &&
            checkpoint.StorageProvider is { Length: > 0 and <= 32 } && checkpoint.ProviderState is not { Length: > 2097152 });

    private const int MaxDestinations = 16;

    /// <summary>
    /// A completion is accepted only if everything it names is part of the RUN'S SNAPSHOT: every replica and every destination result must be one of the
    /// snapshot destinations, none twice, and a run with several destinations must account for every one of them. Nothing the agent says can add a destination.
    /// </summary>
    public static bool IsValidCompletion(BackupTaskCompletionRequest request, BackupConfiguration configuration)
    {
        if (request.LeaseToken == Guid.Empty || request.Destinations is not { Count: <= MaxDestinations } || request.Destinations.Any(d => d is null)) return false;
        if (request.Status == "Paused") return request.Artifact is null && request.Destinations.Count == 0 &&
            request.FailureCode is "Operator" or "TimeRule" or "PausedByNetworkError";
        var snapshot = configuration.Destinations;
        if (request.Status == "Cancelled" && request.FailureCode is not (null or "Cancelled")) return false;
        if (request.Status == "Failed" && (request.FailureCode is null || !BackupFailureCodes.IsAgentReportable(request.FailureCode))) return false;
        if (request.Status is "Failed" or "Cancelled" && request.Artifact is null)
            return !request.Destinations.Any(d => d.ObjectMayRemain) && ValidDestinations(request.Destinations, snapshot, null);
        if (request.Status is not ("Succeeded" or "Failed" or "Cancelled") || request.Artifact is not { } a) return false;
        if (request.Status == "Succeeded" && request.FailureCode is not null) return false;
        if (a.SizeBytes <= 0 || a.CreatedAt == default || a.ChecksumAlgorithm != "sha256" || !Checksum(a.SourceChecksum) || !Checksum(a.ArtifactChecksum) ||
            !ValidEncryption(a) || a.Replicas is null || a.Replicas.Count > snapshot.Count || !Metadata(a, configuration)) return false;
        var policyFailureWithCopies = configuration.EnforceThreeTwoOne && request.Status == "Failed" && request.FailureCode == BackupFailureCodes.StorageUnavailable
            && a.Replicas.Count > 0 && (!BackupReplicationEvaluator.MeetsThreeTwoOne(
                snapshot.Where(destination => a.Replicas.Any(replica => replica.StorageProvider == destination.StorageProvider
                    && replica.StorageConnectionId == destination.StorageConnectionId)).Select(d => (d.MediaClass, d.Site, d.OffsiteVerified)), configuration.SourceSite)
                || a.Replicas.Count < configuration.RequiredVerifiedReplicaCount);
        if (request.Status == "Succeeded" ? a.Replicas.Count == 0 : !policyFailureWithCopies && (a.Replicas.Count != 0 || !request.Destinations.Any(d => d.ObjectMayRemain))) return false;
        if (request.Status == "Succeeded" && configuration.EnforceThreeTwoOne && (a.Replicas.Count < configuration.RequiredVerifiedReplicaCount || !BackupReplicationEvaluator.MeetsThreeTwoOne(
            snapshot.Where(destination => a.Replicas.Any(replica => replica.StorageProvider == destination.StorageProvider
                && replica.StorageConnectionId == destination.StorageConnectionId)).Select(d => (d.MediaClass, d.Site, d.OffsiteVerified)), configuration.SourceSite))) return false;
        return a.Replicas.All(r => r is not null && Destination(snapshot, r.StorageProvider, r.StorageConnectionId) is { } destination && Reference(r.StorageTarget) &&
            (destination.StorageExecutionLocation != BackupStorageExecutionLocation.Agent || r.StorageTarget == (destination.StorageTarget ?? r.StorageProvider)) &&
            (!StorageProviderTypes.External(destination.StorageProvider) || r.StorageTarget == destination.StorageConnectionId?.ToString("D")) &&
            Text(r.Key, StorageObjectKey.MaxLength) && SafeKey(r.Key) && r.SizeBytes == a.SizeBytes && r.ChecksumAlgorithm == a.ChecksumAlgorithm &&
            Checksum(r.Checksum) && r.Checksum.Equals(a.ArtifactChecksum, StringComparison.OrdinalIgnoreCase) && r.VerificationState == "Verified" &&
            r.VerificationMethod is "ReadBack" or "ServerSideChecksum" && r.CreatedAt != default && r.VerifiedAt is not null && r.VerifiedAt >= r.CreatedAt) &&
            a.Replicas.Select(r => (r.StorageProvider, r.StorageTarget, r.Key)).Distinct().Count() == a.Replicas.Count &&
            a.Replicas.Select(r => Destination(snapshot, r.StorageProvider, r.StorageConnectionId)).Distinct().Count() == a.Replicas.Count &&
            ValidDestinations(request.Destinations, snapshot, a);
    }

    /// <summary>The snapshot destination a report or replica names. An agent that does not name the connection (it predates multi-destination runs) is understood only where the provider is unambiguous.</summary>
    private static BackupDestination? Destination(IReadOnlyList<BackupDestination> snapshot, string? provider, Guid? connectionId)
    {
        var candidates = snapshot.Where(d => d.StorageProvider == provider && (connectionId is null || d.StorageConnectionId == connectionId)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Empty is legal only when nothing was transferred (a failure before the transfer).
    /// Otherwise every snapshot destination appears exactly once, and a destination is Verified exactly when the artifact has its replica.
    /// </summary>
    private static bool ValidDestinations(IReadOnlyList<BackupTaskDestinationResult> reported, IReadOnlyList<BackupDestination> snapshot, BackupTaskArtifact? artifact)
    {
        if (reported.Count == 0) return artifact is null;
        if (reported.Count != snapshot.Count) return false;
        var seen = new HashSet<BackupDestination>();
        foreach (var d in reported)
        {
            if (d is null || Destination(snapshot, d.StorageProvider, d.StorageConnectionId) is not { } destination || !seen.Add(destination)) return false;
            if (d.State is not ("Verified" or "Failed" or "Cancelled") || d.Upload is not ("Succeeded" or "Failed" or "NotAttempted") ||
                d.Verification is not ("Verified" or "Failed" or "NotRun") || d.StartedAt == default || d.CompletedAt < d.StartedAt) return false;
            if (d.Key is not null && !(Text(d.Key, StorageObjectKey.MaxLength) && SafeKey(d.Key) && d.Key.Length > 0)) return false;
            if (d.ObjectMayRemain && (d.Key is null || d.Upload != "Succeeded" || d.State == "Verified")) return false;
            if (d.State == "Verified")
            {
                var replica = artifact?.Replicas.FirstOrDefault(r => Destination(snapshot, r.StorageProvider, r.StorageConnectionId) == destination);
                if (replica is null || d.Upload != "Succeeded" || d.Verification != "Verified" || d.FailureCode is not null || d.Key != replica.Key) return false;
            }
            else if (d.Verification == "Verified" || d.FailureCode is null ||
                (d.State == "Cancelled" ? d.FailureCode != "Cancelled" : !BackupFailureCodes.IsAgentReportable(d.FailureCode))) return false;
        }
        // Every replica the artifact carries belongs to a destination that reported Verified: nothing is published that was not accounted for.
        return artifact is null ? reported.All(d => d.State != "Verified") : reported.Count(d => d.State == "Verified") == artifact.Replicas.Count;
    }

    private static bool Checksum(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidEncryption(BackupTaskArtifact a) => a.Format switch
    {
        ArtifactFormats.Plain => a.EncryptionKeyId is null && a.EncryptionKeyVersion is null && a.EncryptionWrappedDekBase64 is null && a.EncryptionRecipients.Count == 0,
        ArtifactFormats.Bpe2 => a.EncryptionKeyId is { Length: 32 } id && id.All(c => char.IsAsciiHexDigitLower(c)) && a.EncryptionKeyVersion is >= 1 &&
            a.EncryptionWrappedDekBase64 is { Length: > 0 and <= 4096 } dek && Convert.TryFromBase64String(dek, new byte[3072], out _) && ValidRecipients(a),
        _ => false
    };

    /// <summary>One entry per recipient type (at most the three that exist), each with a hex key id, and exactly one tenant-wrap entry that is the artifact's own key.</summary>
    private static bool ValidRecipients(BackupTaskArtifact a) =>
        a.EncryptionRecipients is { Count: >= 1 and <= 3 } recipients &&
        recipients.All(r => r.Type is BackupRecipientTypes.TenantWrap or BackupRecipientTypes.PlatformRecovery or BackupRecipientTypes.TenantRecovery &&
            r.KeyId is { Length: 32 } key && key.All(c => char.IsAsciiHexDigitLower(c)) && r.KeyVersion >= 1) &&
        recipients.Select(r => r.Type).Distinct().Count() == recipients.Count &&
        recipients.SingleOrDefault(r => r.Type == BackupRecipientTypes.TenantWrap) is { } wrap && wrap.KeyId == a.EncryptionKeyId && wrap.KeyVersion == a.EncryptionKeyVersion;

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
