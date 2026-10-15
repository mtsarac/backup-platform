using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Discovery;

public sealed record DiscoveryMergeResult(
    IReadOnlyList<DiscoveredInstance> NewInstances, IReadOnlyList<DiscoveredDatabase> NewDatabases, IReadOnlyList<DiscoveredDatabase> PurgedDatabases);

/// <summary>
/// Folds one validated scan report into the stored inventory by mutating the (tracked) entities it is given. Pure: no I/O, so the rules are
/// testable without a database. Nothing is ever deleted because it was not seen; see <see cref="DiscoveredInstance.MarkNotSeen"/>.
/// </summary>
public static class DiscoveryMerger
{
    /// <param name="existingInstances">All stored instances of the agent.</param>
    /// <param name="existingDatabases">All stored databases of those instances.</param>
    public static DiscoveryMergeResult Apply(
        TenantId tenantId, AgentId agentId, IReadOnlyCollection<DiscoveredInstance> existingInstances, IReadOnlyCollection<DiscoveredDatabase> existingDatabases,
        AgentDiscoveryReport report, DateTimeOffset now)
    {
        var newInstances = new List<DiscoveredInstance>();
        var newDatabases = new List<DiscoveredDatabase>();
        var purged = new List<DiscoveredDatabase>();
        if (report.Outcome == DiscoveryOutcomes.Failed)
        {
            return new DiscoveryMergeResult(newInstances, newDatabases, purged);
        }

        var instances = existingInstances.ToDictionary(instance => instance.InstanceKey, StringComparer.Ordinal);
        var databasesByInstance = existingDatabases.ToLookup(database => database.InstanceId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reported in report.Instances)
        {
            seen.Add(reported.InstanceKey);
            var observation = ToObservation(reported);
            if (instances.TryGetValue(reported.InstanceKey, out var instance))
            {
                instance.Observe(observation, now);
            }
            else
            {
                instance = new DiscoveredInstance(Guid.NewGuid(), tenantId, agentId, reported.InstanceKey, observation, now);
                newInstances.Add(instance);
            }
            if (reported.Databases is { } databases)
            {
                MergeDatabases(tenantId, instance, databasesByInstance[instance.Id].ToList(), databases, now, newDatabases, purged);
                instance.MarkDatabasesChecked(now);
            }
        }

        // Absence only counts for sources this scan actually read: a missing docker CLI must not make every container "missing".
        var scanned = report.Sources.Where(source => source.Status == DiscoverySourceStates.Scanned).Select(source => source.Source).ToHashSet(StringComparer.Ordinal);
        foreach (var instance in existingInstances.Where(instance => !seen.Contains(instance.InstanceKey) && scanned.Contains(instance.Source)))
        {
            instance.MarkNotSeen(now);
        }
        return new DiscoveryMergeResult(newInstances, newDatabases, purged);
    }

    private static void MergeDatabases(
        TenantId tenantId, DiscoveredInstance instance, List<DiscoveredDatabase> stored, IReadOnlyList<AgentDiscoveredDatabase> reported, DateTimeOffset now,
        List<DiscoveredDatabase> newDatabases, List<DiscoveredDatabase> purged)
    {
        var byName = stored.ToDictionary(database => database.Name, StringComparer.Ordinal);
        foreach (var database in reported)
        {
            var observation = new DatabaseObservation(database.Name, database.IsSystem, database.State, database.FullBackup, database.FullBackupReasonCode);
            if (byName.TryGetValue(database.Name, out var existing))
            {
                existing.Observe(observation, now);
            }
            else
            {
                newDatabases.Add(new DiscoveredDatabase(Guid.NewGuid(), tenantId, instance.Id, observation, now));
            }
        }
        var reportedNames = reported.Select(database => database.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in stored.Where(database => !reportedNames.Contains(database.Name)))
        {
            gone.MarkMissing();
            if (now - gone.LastSeenAt > DiscoveredDatabase.MissingRetention)
            {
                purged.Add(gone);
            }
        }
    }

    private static InstanceObservation ToObservation(AgentDiscoveredInstance instance) => new(
        instance.Engine, instance.Source, instance.DisplayName, instance.InstanceName, instance.Host, instance.Port, instance.Version, instance.Edition,
        instance.HostPlatform, instance.ContainerName, instance.ContainerImage, instance.Reachability, instance.Access, instance.AccessReasonCode,
        instance.FullBackup, instance.FullBackupReasonCode);
}
