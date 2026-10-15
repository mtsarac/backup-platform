using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Application.Discovery;

/// <summary>
/// The trust boundary of an agent's discovery report: closed vocabularies, bounded cardinalities and string lengths, no control characters.
/// Returns the first offending field path so the agent gets a precise but non-echoing error.
/// </summary>
public static class DiscoveryReportValidator
{
    private static readonly HashSet<string> Engines = [DiscoveryEngines.MsSql, DiscoveryEngines.PostgreSql];
    private static readonly HashSet<string> Sources = [DiscoverySources.Service, DiscoverySources.Listener, DiscoverySources.Docker];
    private static readonly HashSet<string> SourceStates = [DiscoverySourceStates.Scanned, DiscoverySourceStates.Unavailable, DiscoverySourceStates.Disabled, DiscoverySourceStates.Failed];
    private static readonly HashSet<string> Outcomes = [DiscoveryOutcomes.Completed, DiscoveryOutcomes.Partial, DiscoveryOutcomes.Failed];
    private static readonly HashSet<string> Reachabilities = [DiscoveryReachability.Reachable, DiscoveryReachability.Unreachable, DiscoveryReachability.Unknown];
    private static readonly HashSet<string> Accesses =
    [
        DiscoveryAccess.CredentialsRequired, DiscoveryAccess.Authenticated, DiscoveryAccess.AuthenticationFailed,
        DiscoveryAccess.Unreachable, DiscoveryAccess.Error, DiscoveryAccess.Unknown
    ];
    private static readonly HashSet<string> Supports = [DiscoverySupportNames.Supported, DiscoverySupportNames.Unsupported, DiscoverySupportNames.Unknown];
    private static readonly HashSet<string> DatabaseStates = [DiscoveryDatabaseStates.Online, DiscoveryDatabaseStates.Offline, DiscoveryDatabaseStates.Unknown];

    public static bool TryValidate(AgentDiscoveryReport? report, out string? invalidField)
    {
        invalidField = Check(report);
        return invalidField is null;
    }

    public static bool IsValidReference(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string? Check(AgentDiscoveryReport? report)
    {
        if (report is null) return "Report";
        if (!Outcomes.Contains(report.Outcome ?? string.Empty)) return "Outcome";
        if (!IsCode(report.FailureCode, optional: true)) return "FailureCode";
        if (report.Sources is not { Count: <= 8 } sources) return "Sources";
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            if (source is null || !Sources.Contains(source.Source ?? string.Empty) || !SourceStates.Contains(source.Status ?? string.Empty)
                || !IsCode(source.ReasonCode, optional: true) || sources.Take(index).Any(other => other.Source == source.Source))
                return $"Sources[{index}]";
        }
        if (report.Instances is not { Count: <= DiscoveryProtocol.MaximumInstancesPerReport } instances) return "Instances";
        if (instances.Sum(instance => instance?.Databases?.Count ?? 0) > DiscoveryProtocol.MaximumDatabasesPerReport) return "Instances";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < instances.Count; index++)
        {
            var field = CheckInstance(instances[index], $"Instances[{index}]");
            if (field is not null) return field;
            if (!keys.Add(instances[index].InstanceKey)) return $"Instances[{index}].InstanceKey";
        }
        return null;
    }

    private static string? CheckInstance(AgentDiscoveredInstance? instance, string path)
    {
        if (instance is null) return path;
        if (!IsText(instance.InstanceKey, 200)) return $"{path}.InstanceKey";
        if (!Engines.Contains(instance.Engine ?? string.Empty)) return $"{path}.Engine";
        if (!Sources.Contains(instance.Source ?? string.Empty)) return $"{path}.Source";
        if (!IsText(instance.DisplayName, 200)) return $"{path}.DisplayName";
        if (!IsText(instance.InstanceName, 128, optional: true)) return $"{path}.InstanceName";
        if (!IsText(instance.Host, 255) || instance.Host.Any(char.IsWhiteSpace)) return $"{path}.Host";
        if (instance.Port is < 1 or > 65535) return $"{path}.Port";
        if (!IsText(instance.Version, 128, optional: true)) return $"{path}.Version";
        if (!IsText(instance.Edition, 128, optional: true)) return $"{path}.Edition";
        if (!IsText(instance.HostPlatform, 128, optional: true)) return $"{path}.HostPlatform";
        if (!IsText(instance.ContainerName, 256, optional: true)) return $"{path}.ContainerName";
        if (!IsText(instance.ContainerImage, 256, optional: true)) return $"{path}.ContainerImage";
        if (!Reachabilities.Contains(instance.Reachability ?? string.Empty)) return $"{path}.Reachability";
        if (!Accesses.Contains(instance.Access ?? string.Empty)) return $"{path}.Access";
        if (!IsCode(instance.AccessReasonCode, optional: true)) return $"{path}.AccessReasonCode";
        if (!Supports.Contains(instance.FullBackup ?? string.Empty)) return $"{path}.FullBackup";
        if (!IsCode(instance.FullBackupReasonCode, optional: true)) return $"{path}.FullBackupReasonCode";
        if (instance.Databases is null) return null;
        if (instance.Databases.Count > DiscoveryProtocol.MaximumDatabasesPerInstance) return $"{path}.Databases";
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < instance.Databases.Count; index++)
        {
            var database = instance.Databases[index];
            var databasePath = $"{path}.Databases[{index}]";
            if (database is null || !IsText(database.Name, 256) || !names.Add(database.Name)) return $"{databasePath}.Name";
            if (!DatabaseStates.Contains(database.State ?? string.Empty)) return $"{databasePath}.State";
            if (!Supports.Contains(database.FullBackup ?? string.Empty)) return $"{databasePath}.FullBackup";
            if (!IsCode(database.FullBackupReasonCode, optional: true)) return $"{databasePath}.FullBackupReasonCode";
        }
        return null;
    }

    private static bool IsText(string? value, int maximum, bool optional = false) =>
        value is null ? optional : value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);

    /// <summary>Reason and failure codes are short machine tokens, never free text.</summary>
    private static bool IsCode(string? value, bool optional) =>
        value is null ? optional : value.Length is > 0 and <= 64 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
}
