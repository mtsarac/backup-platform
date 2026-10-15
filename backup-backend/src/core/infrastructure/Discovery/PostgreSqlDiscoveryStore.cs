using System.Text.Json;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Discovery;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BackupPlatform.Infrastructure.Discovery;

/// <summary>
/// Discovery inventory under explicit platform operations; every statement names its tenant.
/// </summary>
/// <remarks>
/// All writes concerning one agent (scan claim, report, rescan request, settings) run in one transaction that first locks the agent's row
/// (<c>FOR NO KEY UPDATE</c>, which does not block the heartbeat's foreign-key checks, same as the capability store). Concurrent backend nodes
/// therefore apply them one after another against the committed state: a claim cannot be taken twice, a stale report finds another scan id and is
/// refused, and creating the state row cannot race. The row is located by tenant and agent together.
/// </remarks>
public sealed class PostgreSqlDiscoveryStore(DbContextOptions<PlatformDbContext> options) : IDiscoveryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int ScanTimeoutSeconds = 120;

    private const string LockAgentSql = """
        SELECT "Id" AS "Value" FROM agents WHERE "Id" = @agent AND "TenantId" = @tenant FOR NO KEY UPDATE
        """;

    public async Task<string?> GetDueReasonAsync(AgentIdentity caller, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var state = await context.DiscoveryAgentStates.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == caller.TenantId && row.AgentId == caller.AgentId, cancellationToken);
        if (state is not null)
        {
            return state.DueReason(now, DiscoveryScanReasons.Periodic);
        }
        if (!await SupportsDiscoveryAsync(context, caller.TenantId, caller.AgentId, cancellationToken))
        {
            return null;
        }
        context.DiscoveryAgentStates.Add(DiscoveryAgentState.CreateInitial(caller.TenantId, caller.AgentId, DiscoveryScanReasons.Initial, now));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgreSqlBackupRunStore.IsUniqueViolation(exception))
        {
            // A concurrent heartbeat created the same row, with the same initial request.
        }
        return DiscoveryScanReasons.Initial;
    }

    public async Task<AgentDiscoveryPlan?> ClaimAsync(AgentIdentity caller, Guid scanId, double jitter, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, caller.TenantId, caller.AgentId, cancellationToken))
        {
            return null;
        }
        var state = await context.DiscoveryAgentStates.SingleOrDefaultAsync(row => row.TenantId == caller.TenantId && row.AgentId == caller.AgentId, cancellationToken);
        if (state is null || !state.TryClaim(now, scanId, DiscoveryScanReasons.Periodic, jitter, out var reason))
        {
            return null;
        }
        var onlyInstanceKey = state.TakeRequestedInstanceKey();
        var bindings = await context.DiscoveredInstances.AsNoTracking()
            .Where(instance => instance.TenantId == caller.TenantId && instance.AgentId == caller.AgentId
                && (instance.CredentialReference != null || instance.IntegratedSecurity))
            .OrderBy(instance => instance.InstanceKey)
            .Select(instance => new AgentDiscoveryBinding
            {
                InstanceKey = instance.InstanceKey,
                Engine = instance.Engine,
                CredentialReference = instance.CredentialReference,
                IntegratedSecurity = instance.IntegratedSecurity,
                Encrypt = instance.Encrypt,
                TrustServerCertificate = instance.TrustServerCertificate
            })
            .ToListAsync(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentDiscoveryPlan
        {
            ScanId = scanId,
            Reason = reason,
            IncludeDocker = state.IncludeDocker,
            TimeoutSeconds = ScanTimeoutSeconds,
            OnlyInstanceKey = onlyInstanceKey,
            Bindings = bindings
        };
    }

    public async Task<ReportStatus> ApplyReportAsync(
        AgentIdentity caller, Guid scanId, AgentDiscoveryReport report, string sourcesJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, caller.TenantId, caller.AgentId, cancellationToken))
        {
            return ReportStatus.AgentNotFound;
        }
        var state = await context.DiscoveryAgentStates.SingleOrDefaultAsync(row => row.TenantId == caller.TenantId && row.AgentId == caller.AgentId, cancellationToken);
        if (state is null || !state.Complete(scanId, now, report.Outcome, report.FailureCode, sourcesJson))
        {
            return ReportStatus.Superseded;
        }
        if (report.Outcome != DiscoveryOutcomes.Failed)
        {
            var instances = await context.DiscoveredInstances
                .Where(instance => instance.TenantId == caller.TenantId && instance.AgentId == caller.AgentId)
                .ToListAsync(cancellationToken);
            var instanceIds = instances.Select(instance => instance.Id).ToList();
            var databases = await context.DiscoveredDatabases
                .Where(database => database.TenantId == caller.TenantId && instanceIds.Contains(database.InstanceId))
                .ToListAsync(cancellationToken);
            var merge = DiscoveryMerger.Apply(caller.TenantId, caller.AgentId, instances, databases, report, now);
            context.DiscoveredInstances.AddRange(merge.NewInstances);
            context.DiscoveredDatabases.AddRange(merge.NewDatabases);
            context.DiscoveredDatabases.RemoveRange(merge.PurgedDatabases);
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReportStatus.Applied;
    }

    public async Task<IReadOnlyList<DiscoveryAgentStatus>> ListAgentsAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var states = await context.DiscoveryAgentStates.AsNoTracking().Where(state => state.TenantId == tenantId).ToListAsync(cancellationToken);
        var counts = await InstanceCountsAsync(context, tenantId, cancellationToken);
        return states.OrderBy(state => state.AgentId.Value).Select(state => ToStatus(state, counts.GetValueOrDefault(state.AgentId.Value), now)).ToList();
    }

    public async Task<IReadOnlyList<DiscoveryInstanceSummary>> ListInstancesAsync(
        TenantId tenantId, Guid? agentId, string? engine, bool includeMissing, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var query = context.DiscoveredInstances.AsNoTracking().Where(instance => instance.TenantId == tenantId);
        if (agentId is { } agent)
        {
            var typed = new AgentId(agent);
            query = query.Where(instance => instance.AgentId == typed);
        }
        if (engine is not null)
        {
            query = query.Where(instance => instance.Engine == engine);
        }
        if (!includeMissing)
        {
            query = query.Where(instance => instance.Presence == DiscoveryPresence.Present);
        }
        var instances = await query.OrderBy(instance => instance.DisplayName).ThenBy(instance => instance.InstanceKey).ToListAsync(cancellationToken);
        var ids = instances.Select(instance => instance.Id).ToList();
        var counts = await context.DiscoveredDatabases.AsNoTracking()
            .Where(database => database.TenantId == tenantId && ids.Contains(database.InstanceId) && database.Presence == DiscoveryPresence.Present)
            .GroupBy(database => database.InstanceId)
            .Select(group => new
            {
                group.Key,
                Total = group.Count(),
                User = group.Count(database => !database.IsSystem),
                Eligible = group.Count(database => !database.IsSystem && database.State == DiscoveryDatabaseStates.Online && database.FullBackup != DiscoverySupportNames.Unsupported)
            })
            .ToDictionaryAsync(row => row.Key, cancellationToken);
        return instances
            .Select(instance => ToSummary(instance, counts.TryGetValue(instance.Id, out var row) ? new DiscoveryDatabaseCounts(row.Total, row.User, row.Eligible) : new DiscoveryDatabaseCounts(0, 0, 0)))
            .ToList();
    }

    public async Task<DiscoveryInstanceDetail?> GetInstanceAsync(TenantId tenantId, Guid instanceId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var instance = await context.DiscoveredInstances.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return null;
        }
        var databases = await context.DiscoveredDatabases.AsNoTracking()
            .Where(database => database.TenantId == tenantId && database.InstanceId == instanceId)
            .OrderBy(database => database.Name)
            .ToListAsync(cancellationToken);
        // The job configuration is a JSON value, so the match is decided in memory over the jobs of this one agent.
        var jobs = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.AgentId == instance.AgentId).ToListAsync(cancellationToken);
        var present = databases.Where(database => database.Presence == DiscoveryPresence.Present).ToList();
        var counts = new DiscoveryDatabaseCounts(
            present.Count, present.Count(database => !database.IsSystem),
            present.Count(database => !database.IsSystem && database.State == DiscoveryDatabaseStates.Online && database.FullBackup != DiscoverySupportNames.Unsupported));
        var views = databases.Select(database => new DiscoveryDatabaseView(
            database.Name, database.IsSystem, database.State, database.FullBackup, database.FullBackupReasonCode, database.Presence.ToString(), database.LastSeenAt,
            jobs.Where(job => DiscoveryJobMatcher.Covers(job.Configuration, instance, database.Name))
                .Select(job => new DiscoveryJobReference(job.Id, job.Name, job.Enabled)).ToList())).ToList();
        return new DiscoveryInstanceDetail(ToSummary(instance, counts), views);
    }

    public async Task<RescanOutcome> RequestRescanAsync(
        TenantId tenantId, AgentId? agentId, Guid? instanceId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        string? instanceKey = null;
        if (instanceId is { } id)
        {
            var instance = await context.DiscoveredInstances.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken);
            if (instance is null)
            {
                return new RescanOutcome(RescanStatus.NotFound);
            }
            if (!instance.HasBinding)
            {
                return new RescanOutcome(RescanStatus.CredentialsRequired);
            }
            agentId = instance.AgentId;
            instanceKey = instance.InstanceKey;
        }
        if (agentId is null || !await LockAgentAsync(context, tenantId, agentId, cancellationToken))
        {
            return new RescanOutcome(RescanStatus.NotFound);
        }
        if (!await SupportsDiscoveryAsync(context, tenantId, agentId, cancellationToken))
        {
            return new RescanOutcome(RescanStatus.Unsupported);
        }
        var state = await context.DiscoveryAgentStates.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.AgentId == agentId, cancellationToken);
        if (state is null)
        {
            state = DiscoveryAgentState.CreateInitial(tenantId, agentId, DiscoveryScanReasons.Initial, now);
            context.DiscoveryAgentStates.Add(state);
        }
        else if (!state.Enabled)
        {
            return new RescanOutcome(RescanStatus.Disabled);
        }
        else if (!state.CanRequestManual(now))
        {
            return new RescanOutcome(RescanStatus.TooSoon, RetryAfter: state.ManualRetryAfter(now));
        }
        state.RequestScan(DiscoveryScanReasons.Manual, instanceKey, now);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.User, AuditActions.DiscoveryRescanRequested, tenantId, "agent", agentId.Value.ToString("D"),
            new Dictionary<string, object?> { ["result"] = "requested", ["instanceKey"] = instanceKey }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RescanOutcome(RescanStatus.Requested, ToStatus(state, await InstanceCountAsync(context, tenantId, agentId, cancellationToken), now));
    }

    public async Task<DiscoveryAgentStatus?> ChangeSettingsAsync(
        TenantId tenantId, AgentId agentId, bool enabled, int scanIntervalMinutes, bool includeDocker, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, agentId, cancellationToken))
        {
            return null;
        }
        var state = await context.DiscoveryAgentStates.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.AgentId == agentId, cancellationToken);
        if (state is null)
        {
            state = DiscoveryAgentState.CreateInitial(tenantId, agentId, DiscoveryScanReasons.Initial, now);
            context.DiscoveryAgentStates.Add(state);
        }
        state.ChangeSettings(enabled, scanIntervalMinutes, includeDocker, now);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.TenantAdministrator, AuditActions.DiscoverySettingsChanged, tenantId, "agent", agentId.Value.ToString("D"),
            new Dictionary<string, object?> { ["result"] = "succeeded", ["enabled"] = enabled, ["scanIntervalMinutes"] = scanIntervalMinutes, ["includeDocker"] = includeDocker }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToStatus(state, await InstanceCountAsync(context, tenantId, agentId, cancellationToken), now);
    }

    public async Task<BindOutcome> BindCredentialAsync(
        TenantId tenantId, Guid instanceId, string? credentialReference, bool integratedSecurity, bool encrypt, bool trustServerCertificate,
        UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var instance = await context.DiscoveredInstances.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return new BindOutcome(BindStatus.NotFound);
        }
        try
        {
            instance.Bind(credentialReference, integratedSecurity, encrypt, trustServerCertificate);
        }
        catch (ArgumentException)
        {
            return new BindOutcome(BindStatus.Invalid);
        }
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.User, AuditActions.DiscoveryCredentialBound, tenantId, "discovery_instance", instanceId.ToString("D"),
            new Dictionary<string, object?>
            {
                ["result"] = "succeeded", ["credentialReference"] = credentialReference, ["integratedSecurity"] = integratedSecurity,
                ["encrypt"] = encrypt, ["trustServerCertificate"] = trustServerCertificate
            }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BindOutcome(BindStatus.Bound, ToSummary(instance, await CountsAsync(context, tenantId, instanceId, cancellationToken)));
    }

    private static async Task<bool> LockAgentAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        var locked = await context.Database.SqlQueryRaw<Guid>(LockAgentSql,
            new NpgsqlParameter("agent", NpgsqlDbType.Uuid) { Value = agentId.Value },
            new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = tenantId.Value })
            .ToListAsync(cancellationToken);
        return locked.Count != 0;
    }

    /// <summary>The backend only asks for scans an agent said it can run; the capability is the agent's claim, stored as reported.</summary>
    private static Task<bool> SupportsDiscoveryAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken) =>
        context.AgentCapabilities.AsNoTracking().AnyAsync(record => record.TenantId == tenantId && record.AgentId == agentId
            && record.Identifier == DiscoveryProtocol.Capability && record.Support == CapabilitySupport.Supported, cancellationToken);

    private static async Task<Dictionary<Guid, int>> InstanceCountsAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken) =>
        await context.DiscoveredInstances.AsNoTracking()
            .Where(instance => instance.TenantId == tenantId && instance.Presence == DiscoveryPresence.Present)
            .GroupBy(instance => instance.AgentId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key.Value, row => row.Count, cancellationToken);

    private static Task<int> InstanceCountAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken) =>
        context.DiscoveredInstances.AsNoTracking()
            .CountAsync(instance => instance.TenantId == tenantId && instance.AgentId == agentId && instance.Presence == DiscoveryPresence.Present, cancellationToken);

    private static async Task<DiscoveryDatabaseCounts> CountsAsync(PlatformDbContext context, TenantId tenantId, Guid instanceId, CancellationToken cancellationToken)
    {
        var present = await context.DiscoveredDatabases.AsNoTracking()
            .Where(database => database.TenantId == tenantId && database.InstanceId == instanceId && database.Presence == DiscoveryPresence.Present)
            .ToListAsync(cancellationToken);
        return new DiscoveryDatabaseCounts(
            present.Count, present.Count(database => !database.IsSystem),
            present.Count(database => !database.IsSystem && database.State == DiscoveryDatabaseStates.Online && database.FullBackup != DiscoverySupportNames.Unsupported));
    }

    private static DiscoveryAgentStatus ToStatus(DiscoveryAgentState state, int instanceCount, DateTimeOffset now)
    {
        var scanState = state.StateAt(now);
        return new DiscoveryAgentStatus(
            state.AgentId.Value, state.Enabled, state.ScanIntervalMinutes, state.IncludeDocker, scanState.ToString(),
            scanState == DiscoveryScanState.Running ? state.ScanReason : state.ScanRequestReason,
            state.LastScanAt, state.LastSuccessfulScanAt, state.LastOutcome, state.LastFailureCode, state.NextScanDueAt, instanceCount,
            JsonSerializer.Deserialize<List<DiscoverySourceView>>(state.SourcesJson, Json) ?? []);
    }

    private static DiscoveryInstanceSummary ToSummary(DiscoveredInstance instance, DiscoveryDatabaseCounts counts) => new(
        instance.Id, instance.AgentId.Value, instance.InstanceKey, instance.Engine, instance.Source, instance.DisplayName, instance.InstanceName, instance.Host, instance.Port,
        instance.Version, instance.Edition, instance.HostPlatform, instance.ContainerName, instance.ContainerImage, instance.Presence.ToString(), instance.Reachability,
        instance.Access, instance.AccessReasonCode, instance.FullBackup, instance.FullBackupReasonCode,
        new DiscoveryCredentialView(instance.HasBinding, instance.CredentialReference, instance.IntegratedSecurity, instance.Encrypt, instance.TrustServerCertificate),
        instance.FirstSeenAt, instance.LastSeenAt,
        instance.LastChangeKind is { } kind && instance.LastChangeAt is { } at ? new DiscoveryLastChangeView(kind.ToString(), at) : null,
        instance.DatabasesCheckedAt, counts);
}
