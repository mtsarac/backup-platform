using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using NpgsqlTypes;

namespace BackupPlatform.Api.Endpoints;

public static class AgentLogEndpoints
{
    public static IEndpointRouteBuilder MapAgentLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAgentLogIngest();
        endpoints.MapTenantGroup().MapGet("/agent-logs", QueryAsync);
        return endpoints;
    }

    private static async Task<IResult> QueryAsync(ITenantContext tenant, PlatformDbContext db, Guid? agentId, Guid? jobId, Guid? runId,
        string? level, DateTimeOffset? from, DateTimeOffset? to, string? text, CancellationToken cancellationToken)
    {
        if (text?.Length > 200 || (level is not null && level is not ("Information" or "Warning")) || (from > to)) return Results.BadRequest();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT "Id", "AgentId", "Timestamp", "Code", "Message", "Level", "JobId", "RunId" FROM agent_logs
            WHERE "TenantId" = @tenant AND (@agent IS NULL OR "AgentId" = @agent)
              AND (@job IS NULL OR "JobId" = @job) AND (@run IS NULL OR "RunId" = @run)
              AND (@level IS NULL OR "Level" = @level) AND (@from IS NULL OR "Timestamp" >= @from)
              AND (@to IS NULL OR "Timestamp" <= @to) AND (@text IS NULL OR position(lower(@text) in lower("Message")) > 0)
            ORDER BY "Timestamp" DESC, "Id" DESC LIMIT 1000
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant.TenantId!.Value);
        command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Uuid) { Value = (object?)agentId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("job", NpgsqlDbType.Uuid) { Value = (object?)jobId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("run", NpgsqlDbType.Uuid) { Value = (object?)runId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("level", NpgsqlDbType.Text) { Value = (object?)level ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)from?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)to?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = (object?)text ?? DBNull.Value });
        var result = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new { id = reader.GetGuid(0), agentId = reader.GetGuid(1), timestamp = reader.GetFieldValue<DateTimeOffset>(2), code = reader.GetString(3), message = reader.GetString(4), level = reader.GetString(5), jobId = reader.IsDBNull(6) ? (Guid?)null : reader.GetGuid(6), runId = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7) });
        return Results.Ok(result);
    }
}
