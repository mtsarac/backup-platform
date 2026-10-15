using System.Text.Json;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// Turns a pipeline result into the terminal message of a restore attempt. The message carries only identifiers, numbers and fixed text: the
/// failure code and target state are closed vocabularies, the free-text fields come from providers that promise user-safe sentences and are
/// still stripped of control characters and cut to a fixed length, and the whole result is kept below a serialized size the backend accepts
/// (it is stored as one JSON document). Nothing here is ever derived from an exception.
/// </summary>
internal static class RestoreTaskResultMapper
{
    /// <summary>The backend stores the result as one JSON value of at most 64 KiB; this leaves room for it to be re-serialized differently.</summary>
    internal const int MaxResultJsonLength = 48 * 1024;

    private const int MaxId = 64;
    private const int MaxMessage = 300;
    private const int MaxName = 128;
    private const int MaxState = 32;

    /// <summary>Progressively smaller detail budgets, including real discovered databases.</summary>
    private static readonly (int PreCheck, int Warnings, int FileMappings, int Databases)[] Budgets =
    [
        (32, 16, 128, 128), (16, 8, 64, 64), (8, 4, 16, 16), (4, 2, 4, 4), (2, 1, 1, 1), (0, 0, 0, 0)
    ];

    public const string TruncatedWarning = "result-truncated";

    public static RestoreTaskCompletionRequest Map(RestoreTaskSnapshot task, DatabaseRestorePipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(result);

        var readOnly = task.Purpose is "Precheck" or "Discovery";
        var expectedState = readOnly ? DatabaseRestoreTargetState.NotModified : DatabaseRestoreTargetState.Online;
        // Read-only tasks can never claim a modified target, even when a provider breaks its contract.
        if ((readOnly && result.TargetState != DatabaseRestoreTargetState.NotModified)
            || (result.Status == DatabaseRestoreStatus.Succeeded
                && (result.TargetState != expectedState || (task.Purpose == "Discovery" && result.Discovery is null))))
        {
            return Failure(task.LeaseToken, RestoreAgentFailureCodes.InternalError,
                readOnly ? DatabaseRestoreTargetState.NotModified : DatabaseRestoreTargetState.Unknown);
        }

        var targetState = result.Status != DatabaseRestoreStatus.Succeeded && result.TargetState == DatabaseRestoreTargetState.Online
            ? DatabaseRestoreTargetState.Unknown
            : result.TargetState;

        return new RestoreTaskCompletionRequest
        {
            LeaseToken = task.LeaseToken,
            Status = result.Status.ToString(),
            FailureCode = FailureCodeOf(result),
            TargetState = targetState.ToString(),
            Result = Fit(result, targetState)
        };
    }

    /// <summary>A terminal message for an attempt that stopped without a pipeline result (the snapshot was refused, the pipeline broke its contract).</summary>
    public static RestoreTaskCompletionRequest Failure(Guid leaseToken, string failureCode, DatabaseRestoreTargetState targetState) => new()
    {
        LeaseToken = leaseToken,
        Status = failureCode == RestoreAgentFailureCodes.Cancelled ? nameof(DatabaseRestoreStatus.Cancelled) : nameof(DatabaseRestoreStatus.Failed),
        FailureCode = RestoreAgentFailureCodes.Reportable.Contains(failureCode) || failureCode == RestoreAgentFailureCodes.Cancelled
            ? failureCode
            : RestoreAgentFailureCodes.InternalError,
        TargetState = targetState.ToString(),
        Result = new RestoreTaskResult { TargetStateNote = NoteFor(failureCode == RestoreAgentFailureCodes.Cancelled, targetState) }
    };

    private static string? FailureCodeOf(DatabaseRestorePipelineResult result) => result.Status switch
    {
        DatabaseRestoreStatus.Succeeded => null,
        DatabaseRestoreStatus.Cancelled => RestoreAgentFailureCodes.Cancelled,
        _ => result.Failure is { } failure && RestoreAgentFailureCodes.Reportable.Contains(failure.Code)
            ? failure.Code
            : RestoreAgentFailureCodes.InternalError
    };

    /// <summary>The most generous budget whose serialized form fits; the last budget keeps nothing variable, so what remains is bounded by the fixed field limits and always fits.</summary>
    private static RestoreTaskResult Fit(DatabaseRestorePipelineResult result, DatabaseRestoreTargetState targetState)
    {
        foreach (var budget in Budgets[..^1])
        {
            var mapped = Build(result, targetState, budget.PreCheck, budget.Warnings, budget.FileMappings, budget.Databases);
            if (JsonSerializer.SerializeToUtf8Bytes(mapped, JsonSerializerOptions.Web).Length <= MaxResultJsonLength)
            {
                return mapped;
            }
        }

        var last = Budgets[^1];
        return Build(result, targetState, last.PreCheck, last.Warnings, last.FileMappings, last.Databases);
    }

    private static RestoreTaskResult Build(DatabaseRestorePipelineResult result, DatabaseRestoreTargetState targetState,
        int maxPreCheck, int maxWarnings, int maxMappings, int maxDatabases)
    {
        var preCheck = Select(result.PreCheck, maxPreCheck, item => item.Status is BackupPreCheckStatus.Failed or BackupPreCheckStatus.Warning);
        var warnings = result.Warnings.Take(maxWarnings).Select(warning => new RestoreWarningResult(Text(warning.Code, MaxId), Text(warning.Message, MaxMessage))).ToList();
        var truncated = preCheck.Truncated || result.Warnings.Count > maxWarnings || result.FileMappings.Count > maxMappings
            || result.Discovery?.Databases.Count > maxDatabases;
        if (truncated)
        {
            warnings.Add(new RestoreWarningResult(TruncatedWarning, "Some details of this restore were left out because the report was too large."));
        }

        return new RestoreTaskResult
        {
            PreCheck = preCheck.Items.Select(item => new RestorePreCheckItemResult(
                Text(item.CheckId, MaxId), item.Status.ToString(), Text(item.ReasonCode, MaxId), Text(item.Message, MaxMessage))).ToList(),
            Warnings = warnings,
            SourceBackup = result.Inspection is { } inspection ? MapHeader(inspection.Header) : null,
            TargetServer = result.Plan?.Target.Instance is { } instance
                ? new RestoreTargetServerSummary
                {
                    ServerName = Optional(instance.ServerName, MaxName),
                    InstanceName = Optional(instance.InstanceName, MaxName),
                    Version = Optional(instance.Version, MaxName),
                    Edition = Optional(instance.Edition, MaxName),
                    HostPlatform = Optional(instance.HostPlatform, MaxName)
                }
                : null,
            Discovery = result.Discovery is { } discovery ? MapDiscovery(discovery, maxDatabases) : null,
            FileMappings = result.FileMappings.Take(maxMappings).Select(mapping => new RestoreFileMappingResult
            {
                LogicalName = Text(mapping.LogicalName, MaxName),
                FileType = mapping.Kind.ToString(),
                FileGroupName = Optional(mapping.FileGroupName, MaxName),
                TargetPhysicalName = Optional(mapping.TargetPath.Value, 1024),
                SizeBytes = mapping.SizeBytes
            }).ToList(),
            StartedAt = result.StartedAt,
            FinishedAt = result.FinishedAt,
            RestoreStartedAt = result.RestoreStartedAt,
            RestoreFinishedAt = result.RestoreFinishedAt,
            ChecksumVerified = result.ChecksumVerified,
            BackupVerified = result.BackupVerified,
            DatabaseState = DatabaseStateOf(result.DatabaseState),
            TargetStateNote = NoteFor(result.Status == DatabaseRestoreStatus.Cancelled, targetState)
        };
    }

    private static RestoreDiscoveryResult MapDiscovery(DatabaseInstanceDiscovery discovery, int maxDatabases) => new()
    {
        Instance = new RestoreDiscoveredInstance(
            Text(discovery.Instance.Engine.Value, MaxId), Text(discovery.Instance.ServerName, MaxName),
            Optional(discovery.Instance.InstanceName, MaxName), Optional(discovery.Instance.Version, MaxName),
            Optional(discovery.Instance.Edition, MaxName), Optional(discovery.Instance.HostPlatform, MaxName)),
        Databases = discovery.Databases.Take(maxDatabases).Select(database => new RestoreDiscoveredDatabase(
            Text(database.Name, MaxName), database.IsSystem,
            Enum.IsDefined(database.Availability) ? database.Availability.ToString() : nameof(DatabaseAvailability.Unknown))).ToList()
    };

    private static RestoreSourceBackupSummary MapHeader(RestoreBackupHeader header) => new()
    {
        DatabaseName = Text(header.DatabaseName, MaxName),
        BackupType = header.Kind.ToString(),
        SourceServerName = Optional(header.SourceServerName, MaxName),
        SourceServerVersion = header.SourceServerVersion?.ToString(),
        BackupStartedAt = header.BackupStartedAt,
        BackupFinishedAt = header.BackupFinishedAt,
        Position = header.Position,
        BackupSizeBytes = header.BackupSizeBytes,
        CompatibilityLevel = header.CompatibilityLevel,
        Collation = Optional(header.Collation, MaxName),
        IsCopyOnly = header.IsCopyOnly,
        HasChecksums = header.HasChecksums,
        IsCompressed = header.IsCompressed
    };

    /// <summary>
    /// Keeps at most <paramref name="max"/> items in their original order, preferring the ones the reader must not miss (failed and warning
    /// items) over passed ones when something has to go.
    /// </summary>
    private static (IReadOnlyList<BackupPreCheckItem> Items, bool Truncated) Select(
        IReadOnlyList<BackupPreCheckItem> items, int max, Func<BackupPreCheckItem, bool> important)
    {
        if (items.Count <= max)
        {
            return (items, false);
        }

        var keep = new HashSet<int>();
        for (var index = 0; index < items.Count && keep.Count < max; index++)
        {
            if (important(items[index]))
            {
                keep.Add(index);
            }
        }

        for (var index = 0; index < items.Count && keep.Count < max; index++)
        {
            keep.Add(index);
        }

        return (items.Where((_, index) => keep.Contains(index)).ToList(), true);
    }

    /// <summary>The engine's database state as a plain identifier (<c>ONLINE</c>, <c>RESTORING</c>, <c>RECOVERY_PENDING</c>); anything else is not trusted and not sent.</summary>
    private static string? DatabaseStateOf(string? state) =>
        state is { Length: > 0 and <= MaxState } && state.All(character => character is >= 'A' and <= 'Z' or '_') ? state : null;

    /// <summary>The fixed sentence for a target that is not simply a healthy online database. Never names the database or the server.</summary>
    private static string? NoteFor(bool cancelled, DatabaseRestoreTargetState targetState) => targetState switch
    {
        DatabaseRestoreTargetState.PartiallyRestored when cancelled =>
            "The restore was cancelled while running. The target database may be partially restored and is not safe to use. Check the target and any retained recovery databases before making changes.",
        DatabaseRestoreTargetState.PartiallyRestored =>
            "The restore failed while running. The target database may be partially restored and is not safe to use. Check the target and any retained recovery databases before making changes.",
        DatabaseRestoreTargetState.Unknown =>
            "The agent could not establish the state of the target database. Check it on the server before using it or restoring again.",
        _ => null
    };

    private static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Text(value, max);

    /// <summary>Control characters become spaces and the text is cut to <paramref name="max"/> characters (never inside a surrogate pair).</summary>
    private static string Text(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var length = Math.Min(value.Length, max);
        if (length < value.Length && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return string.Create(length, value, static (span, source) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = char.IsControl(source[index]) ? ' ' : source[index];
            }
        });
    }
}
