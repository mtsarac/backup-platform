using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using NpgsqlTypes;

namespace BackupPlatform.Api.Endpoints;

/// <summary>An agent's log upload: the agent's identity names the tenant, so this is a platform operation and never a tenant request.</summary>
internal static class AgentLogIngestEndpoint
{
    internal static IEndpointRouteBuilder MapAgentLogIngest(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/agent/logs", IngestAsync)
            .WithMetadata(new AgentApiEndpoint(), new RequestSizeLimitAttribute(64 * 1024))
            .RequireAuthorization(AuthorizationPolicies.Agent).RequireRateLimiting(AgentRateLimitOptions.Policies.Poll);
        return endpoints;
    }

    private static async Task<IResult> IngestAsync(AgentLogEntry[] entries, ICurrentAgent current, DbContextOptions<PlatformDbContext> options, CancellationToken cancellationToken)
    {
        if (entries is null || entries.Length is < 1 or > 100 || entries.Any(e => e is null || e.Id == Guid.Empty || AgentLogEvents.Message(e.Code) is null || e.Timestamp == default
            || (e.Code == "agent.logs_dropped" ? e.DroppedCount <= 0 : e.DroppedCount != 0)))
            return Results.BadRequest();
        var identity = current.Identity!;
        await using var db = PlatformOperationsDbContext.Create(options);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entry in entries)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO agent_logs ("Id", "TenantId", "AgentId", "Timestamp", "Code", "Message", "Level", "JobId", "RunId")
                VALUES (@id, @tenant, @agent, @at, @code, @message, @level, @job, @run)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue("id", entry.Id);
            command.Parameters.AddWithValue("tenant", identity.TenantId.Value);
            command.Parameters.AddWithValue("agent", identity.AgentId.Value);
            command.Parameters.AddWithValue("at", entry.Timestamp.ToUniversalTime());
            command.Parameters.AddWithValue("code", entry.Code);
            command.Parameters.AddWithValue("message", entry.Message);
            command.Parameters.AddWithValue("level", entry.Level);
            command.Parameters.Add(new NpgsqlParameter("job", NpgsqlDbType.Uuid) { Value = (object?)entry.JobId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("run", NpgsqlDbType.Uuid) { Value = (object?)entry.RunId ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }
}
