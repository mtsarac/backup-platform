using System.Text.Json;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Discovery;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BackupPlatform.Infrastructure.Discovery;

/// <summary>Agent-facing discovery (due scans, claims, reports) on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlDiscoveryPlatformStore(DbContextOptions<PlatformDbContext> options) : IDiscoveryPlatformStore
{
    private readonly DiscoveryStoreCore core = new(_ => PlatformOperationsDbContext.Create(options));

    public Task<string?> GetDueReasonAsync(AgentIdentity caller, DateTimeOffset now, CancellationToken cancellationToken) => core.GetDueReasonAsync(caller, now, cancellationToken);
    public Task<AgentDiscoveryPlan?> ClaimAsync(AgentIdentity caller, Guid scanId, DateTimeOffset now, CancellationToken cancellationToken) => core.ClaimAsync(caller, scanId, now, cancellationToken);
    public Task<ReportStatus> ApplyReportAsync(AgentIdentity caller, Guid scanId, AgentDiscoveryReport report, string sourcesJson, double jitter, DateTimeOffset now, CancellationToken cancellationToken) => core.ApplyReportAsync(caller, scanId, report, sourcesJson, jitter, now, cancellationToken);
}
