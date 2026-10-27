using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Agents;

public sealed class PostgreSqlPlatformAgentSettingsStore(DbContextOptions<PlatformDbContext> options) : IPlatformAgentSettingsStore
{
    private const int MaximumAttempts = 4;

    public async Task<PlatformAgentSettingsState?> GetAsync(CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var row = await context.PlatformAgentSettings.AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == PlatformAgentSettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : new PlatformAgentSettingsState(row.ServerUrl, row.EnrollmentTokenLifetimeMinutes, row.UpdatedAt);
    }

    public async Task SaveAsync(string? serverUrl, int enrollmentTokenLifetimeMinutes, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        for (var attempt = 1; ; attempt++)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            var row = await context.PlatformAgentSettings
                .SingleOrDefaultAsync(settings => settings.Id == PlatformAgentSettings.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            var previousServerUrl = row?.ServerUrl;
            var previousLifetime = row?.EnrollmentTokenLifetimeMinutes;
            var changed = false;

            if (row is null)
            {
                context.PlatformAgentSettings.Add(new PlatformAgentSettings(serverUrl, enrollmentTokenLifetimeMinutes, actor, now));
                changed = true;
            }
            else
            {
                changed = row.Set(serverUrl, enrollmentTokenLifetimeMinutes, actor, now);
            }

            if (changed)
            {
                context.AuditEvents.Add(AuditEvent.Record(
                    now, actor, AuditActorKind.PlatformAdmin, AuditActions.AgentSettingsChanged, null,
                    "platform_agent_settings", "singleton",
                    new Dictionary<string, object?>
                    {
                        ["previousServerUrl"] = previousServerUrl,
                        ["serverUrl"] = serverUrl,
                        ["previousEnrollmentTokenLifetimeMinutes"] = previousLifetime,
                        ["enrollmentTokenLifetimeMinutes"] = enrollmentTokenLifetimeMinutes
                    }));
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt < MaximumAttempts)
            {
                // A concurrent insert or update won; re-read and re-apply.
            }
        }
    }
}
