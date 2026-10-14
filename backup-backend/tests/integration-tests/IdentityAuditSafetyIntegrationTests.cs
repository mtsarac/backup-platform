using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class IdentityAuditSafetyIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task AuditReadsAreTenantScopedAndMissingTenantFailsClosed()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var first = await world.CreateTenantAsync();
        var second = await world.CreateTenantAsync();
        var firstEvent = AuditEvent.Record(world.Clock.GetUtcNow(), UserId.System, AuditActorKind.System,
            AuditActions.BackupRunCompleted, first.Tenant.Id, "backup_run", Guid.NewGuid().ToString("D"));
        var secondEvent = AuditEvent.Record(world.Clock.GetUtcNow(), UserId.System, AuditActorKind.System,
            AuditActions.BackupRunCompleted, second.Tenant.Id, "backup_run", Guid.NewGuid().ToString("D"));
        await using (var platform = fixture.CreatePlatformContext())
        {
            platform.AuditEvents.AddRange(firstEvent, secondEvent);
            await platform.SaveChangesAsync();
            platform.Entry(firstEvent).State = EntityState.Modified;
            await Assert.ThrowsAsync<InvalidOperationException>(() => platform.SaveChangesAsync());
        }
        await using var scoped = fixture.CreateContext(first.Tenant.Id);
        var scopedEvents = await scoped.AuditEvents.AsNoTracking().ToListAsync();
        Assert.Contains(scopedEvents, row => row.Id == firstEvent.Id);
        Assert.All(scopedEvents, row => Assert.Equal(first.Tenant.Id, row.TenantId));
        await using var missing = fixture.CreateContext((TenantId?)null);
        Assert.Empty(await missing.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task AuditFailureRollsBackLogoutAndRetryRecordsOnlyOneEvent()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await using var context = fixture.CreatePlatformContext();
        var session = await context.UserSessions.AsNoTracking().SingleAsync(row => row.UserId == setup.Account.UserId);
        var name = "reject_logout_" + Guid.NewGuid().ToString("N");
        await fixture.ExecuteAsync($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."ActorUserId" = '{setup.Account.UserId.Value}'::uuid AND NEW."Action" = 'auth.logout' THEN
                    RAISE EXCEPTION 'Audit unavailable';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION {name}();
            """);
        var store = new PostgreSqlIdentityStore(fixture.Options);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => store.RevokeSessionAsync(session.Id, session.UserId, default));
            Assert.True(await store.IsSessionActiveAsync(session.Id, session.UserId, DateTimeOffset.UtcNow, default));
            Assert.False(await context.AuditEvents.AnyAsync(row => row.Action == AuditActions.UserLogout && row.TargetId == session.Id.ToString("D")));
        }
        finally
        {
            await fixture.ExecuteAsync($"DROP TRIGGER {name} ON audit_events; DROP FUNCTION {name}();");
        }
        await Task.WhenAll(
            store.RevokeSessionAsync(session.Id, session.UserId, default),
            store.RevokeSessionAsync(session.Id, session.UserId, default));
        Assert.False(await store.IsSessionActiveAsync(session.Id, session.UserId, DateTimeOffset.UtcNow, default));
        var audit = await context.AuditEvents.AsNoTracking().SingleAsync(row => row.Action == AuditActions.UserLogout && row.TargetId == session.Id.ToString("D"));
        Assert.Equal(session.UserId, audit.ActorUserId);
        Assert.Null(audit.TenantId);
    }
}
