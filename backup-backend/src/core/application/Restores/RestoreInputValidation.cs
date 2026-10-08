using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Restores;

namespace BackupPlatform.Application.Restores;

/// <summary>A start request after validation: domain values, plus the digest that detects a replayed Idempotency-Key carrying a different request.</summary>
public sealed record ValidatedRestoreRequest(RestoreTarget Target, string DatabaseName, RestoreMode Mode, bool OverwriteConfirmed, RestoreOptions Options, string Fingerprint);

/// <summary>
/// Boundary validation for restores: the user's start request and the agent's progress/completion reports. Errors are field-level and safe:
/// they name the field and a stable code and never echo configuration values or parser exception text.
/// </summary>
public static class RestoreInputValidation
{
    public const int MaxPreCheckItems = 64;
    public const int MaxWarnings = 64;
    public const int MaxFileMappings = 256;
    private static readonly string[] PreCheckStatuses = ["Passed", "Warning", "Failed", "Skipped"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ValidatedRestoreRequest Validate(Guid artifactId, StartRestoreRequest request, RestorePurpose purpose = RestorePurpose.Execution)
    {
        ArgumentNullException.ThrowIfNull(request);
        Check(request.AgentId != Guid.Empty, "agentId");
        Check(request.ReplicaId != Guid.Empty, "replicaId");
        Check(request.PrecheckId != Guid.Empty, "precheckId");
        Check(purpose == RestorePurpose.Execution || request.PrecheckId is null, "precheckId");
        var target = request.Target;
        Check(target is not null, "target");
        // The same vocabulary as a backup configuration, so one agent resolves both the same way.
        Check(Text(target!.Host, 253) && target.Host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '\\' or ':'), "target.host");
        Check(target.Port is null or >= 1 and <= 65535, "target.port");
        Check(target.IntegratedSecurity
            ? target.DatabaseCredentialReference is null
            : Text(target.DatabaseCredentialReference, 64) && target.DatabaseCredentialReference!.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
            "target.databaseCredentialReference");
        if (purpose != RestorePurpose.Discovery)
        {
            try { RestoreOperation.ValidateDatabaseName(request.DatabaseName); }
            catch (ArgumentException) { throw new InvalidRestoreRequestException("databaseName"); }
        }
        else Check(request.DatabaseName == string.Empty, "databaseName");
        var mode = ParseMode(request.Mode);
        // Overwriting is a decision the caller states in so many words: the exact name of the database that will be replaced.
        if (purpose == RestorePurpose.Execution && mode == RestoreMode.ReplaceExistingDatabase)
        {
            if (request.ConfirmOverwrite is null) throw new InvalidRestoreRequestException("confirmOverwrite", "confirmation_required");
            if (!string.Equals(request.ConfirmOverwrite, request.DatabaseName, StringComparison.Ordinal)) throw new InvalidRestoreRequestException("confirmOverwrite", "confirmation_mismatch");
        }
        else if (request.ConfirmOverwrite is not null)
        {
            throw new InvalidRestoreRequestException("confirmOverwrite", "not_applicable");
        }
        var domainTarget = new RestoreTarget(target.Host, target.Port, target.IntegratedSecurity, target.DatabaseCredentialReference, target.Encrypt, target.TrustServerCertificate);
        try { domainTarget.Validate(); }
        catch (ArgumentException) { throw new InvalidRestoreRequestException("target"); }
        var options = new RestoreOptions(request.Options?.TerminateActiveConnections ?? false, request.Options?.VerifyChecksum ?? true, request.Options?.VerifyBackup ?? true);
        var confirmed = purpose == RestorePurpose.Execution && mode == RestoreMode.ReplaceExistingDatabase;
        var fingerprint = Fingerprint(artifactId, request.AgentId, request.ReplicaId, domainTarget, request.DatabaseName, mode, options);
        if (request.PrecheckId is { } precheckId)
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint + ":" + precheckId.ToString("D"))));
        return new ValidatedRestoreRequest(domainTarget, request.DatabaseName, mode, confirmed, options, fingerprint);
    }

    /// <summary>
    /// SHA-256 of a canonical, defaults-resolved form of the request (artifact, agent, replica as asked, target, database, mode, options): the Idempotency-Key
    /// itself is excluded. Two spellings of the same request (an omitted option vs its default) share a digest; any real difference does not.
    /// </summary>
    private static string Fingerprint(Guid artifactId, Guid agentId, Guid? replicaId, RestoreTarget target, string databaseName, RestoreMode mode, RestoreOptions options)
    {
        var canonical = JsonSerializer.Serialize(new CanonicalRequest(artifactId, agentId, replicaId, target.Host, target.Port, target.IntegratedSecurity, target.DatabaseCredentialReference,
            target.Encrypt, target.TrustServerCertificate, databaseName, mode.ToString(), options.TerminateActiveConnections, options.VerifyChecksum, options.VerifyBackup));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("restore-request-v1:" + canonical)));
    }

    private sealed record CanonicalRequest(Guid ArtifactId, Guid AgentId, Guid? ReplicaId, string Host, int? Port, bool IntegratedSecurity, string? CredentialReference,
        bool Encrypt, bool TrustServerCertificate, string DatabaseName, string Mode, bool TerminateActiveConnections, bool VerifyChecksum, bool VerifyBackup);

    /// <summary>Names only: <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> would also accept numeric text such as "1".</summary>
    private static RestoreMode ParseMode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return RestoreMode.NewDatabase;
        foreach (var name in Enum.GetNames<RestoreMode>())
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) return Enum.Parse<RestoreMode>(name);
        throw new InvalidRestoreRequestException("mode");
    }

    // ---- what the agent reports ----

    public static bool IsValidProgress(BackupTaskProgressRequest request) => request.LeaseToken != Guid.Empty && RestoreStages.IsKnown(request.Stage) &&
        request.Stage is not (RestoreStages.Completed or RestoreStages.Failed or RestoreStages.Cancelled) &&
        (request.Percent is null || double.IsFinite(request.Percent.Value) && request.Percent is >= 0 and <= 100);

    public static bool IsValidStart(BackupTaskStartRequest request) => request.LeaseToken != Guid.Empty &&
        Bounded(request.SoftwareVersion, 64) && Bounded(request.ProtocolVersion, 32) && Bounded(request.Platform, 32);

    /// <summary>
    /// Maps the agent's completion to the domain value, enforcing what the domain would otherwise reject as a lifecycle conflict: a malformed report
    /// is a bad request, not a stale lease. The typed result is re-serialised, so only the fields of <see cref="RestoreTaskResult"/> are ever stored.
    /// </summary>
    /// <exception cref="ArgumentException">The completion is not a well-formed report.</exception>
    public static RestoreCompletion ToCompletion(RestoreTaskCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LeaseToken == Guid.Empty) throw new ArgumentException("A lease token is required.");
        var status = request.Status switch
        {
            "Succeeded" => RestoreStatus.Succeeded,
            "Failed" => RestoreStatus.Failed,
            "Cancelled" => RestoreStatus.Cancelled,
            _ => throw new ArgumentException("Invalid terminal outcome.")
        };
        var targetState = ParseTargetState(request.TargetState);
        var failureCode = request.FailureCode;
        switch (status)
        {
            case RestoreStatus.Succeeded:
                var isPreparation = request.Result?.Discovery is not null || request.Result?.PreCheck is { Count: > 0 };
                var validState = isPreparation
                    ? targetState is RestoreTargetState.Online or RestoreTargetState.NotModified
                    : targetState == RestoreTargetState.Online;
                if (failureCode is not null || !validState)
                    throw new ArgumentException("Success requires an online target and no failure code.");
                break;
            case RestoreStatus.Failed:
                if (!RestoreFailureCodes.IsAgentReportable(failureCode)) throw new ArgumentException("Invalid failure category.");
                if (targetState == RestoreTargetState.Online) throw new ArgumentException("A failed restore cannot leave an online restored database.");
                break;
            default:
                if (failureCode is not (null or RestoreFailureCodes.Cancelled)) throw new ArgumentException("Invalid cancellation category.");
                if (targetState == RestoreTargetState.Online) throw new ArgumentException("A cancelled restore cannot leave an online restored database.");
                // One spelling of a cancellation, whichever way the agent sent it, so a repeated completion compares equal.
                failureCode = RestoreFailureCodes.Cancelled;
                break;
        }
        string? resultJson = null;
        if (request.Result is { } result)
        {
            ValidateResult(result);
            resultJson = JsonSerializer.Serialize(result, JsonOptions);
            if (resultJson.Length > RestoreOperation.MaxResultJsonLength) throw new ArgumentException("The restore result is too large.");
        }
        return new RestoreCompletion(status, failureCode, targetState, resultJson);
    }

    /// <summary>The typed result of a stored operation; null when none was reported or the stored text is unreadable.</summary>
    public static RestoreTaskResult? ReadResult(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<RestoreTaskResult>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static RestoreTargetState ParseTargetState(string? value)
    {
        foreach (var name in Enum.GetNames<RestoreTargetState>())
            if (string.Equals(name, value, StringComparison.Ordinal)) return Enum.Parse<RestoreTargetState>(name);
        throw new ArgumentException("Invalid target state.");
    }

    private static void ValidateResult(RestoreTaskResult result)
    {
        if (result.PreCheck is not { Count: <= MaxPreCheckItems } preCheck || result.Warnings is not { Count: <= MaxWarnings } warnings ||
            result.FileMappings is not { Count: <= MaxFileMappings } mappings) throw new ArgumentException("A restore result list is missing or too long.");
        foreach (var item in preCheck)
        {
            Require(item is not null && Safe(item.CheckId, 64, required: true) && PreCheckStatuses.Contains(item.Status, StringComparer.Ordinal) &&
                Safe(item.ReasonCode, 64, required: true) && item.ReasonCode.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') && Safe(item.Message, 512));
        }
        foreach (var warning in warnings) Require(warning is not null && Safe(warning.Code, 64, required: true) && Safe(warning.Message, 512));
        foreach (var mapping in mappings)
        {
            Require(mapping is not null && Safe(mapping.LogicalName, 128, required: true) && Safe(mapping.FileType, 16) && Safe(mapping.FileGroupName, 128) &&
                Safe(mapping.SourcePhysicalName, 1024) && mapping.TargetPhysicalName is not "" && Safe(mapping.TargetPhysicalName, 1024) && mapping.SizeBytes >= 0);
        }
        if (result.SourceBackup is { } source)
        {
            Require(Safe(source.DatabaseName, 128) && Safe(source.BackupType, 32) && Safe(source.SourceServerName, 256) && Safe(source.SourceServerVersion, 64) &&
                Safe(source.Collation, 128) && source.Position >= 0 && source.BackupSizeBytes >= 0 && source.CompatibilityLevel is null or >= 0 and <= 1000);
        }
        if (result.TargetServer is { } server)
            Require(Safe(server.ServerName, 256) && Safe(server.InstanceName, 256) && Safe(server.Version, 64) && Safe(server.Edition, 128) && Safe(server.HostPlatform, 64));
        Require(Safe(result.DatabaseState, 64) && Safe(result.TargetStateNote, 512));
    }

    private static void Require(bool valid)
    {
        if (!valid) throw new ArgumentException("Invalid restore result.");
    }

    /// <summary>
    /// A bounded, printable, well-formed string. Control characters (including NUL, which PostgreSQL's jsonb cannot hold) and lone surrogates are refused:
    /// the report is shown to users and stored as JSON.
    /// </summary>
    private static bool Safe(string? value, int maximum, bool required = false)
    {
        if (value is null) return !required;
        if (value.Length > maximum || required && value.Length == 0) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character)) return false;
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) { index++; continue; }
            if (char.IsSurrogate(character)) return false;
        }
        return true;
    }

    private static bool Bounded(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static void Check(bool valid, string field)
    {
        if (!valid) throw new InvalidRestoreRequestException(field);
    }
}
