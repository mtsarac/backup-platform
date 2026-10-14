using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

public sealed class RestoreAuditIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalOutcomeAndPinReleaseRollBackWithAuditAndReplayWritesOnlyOnce(bool failed)
    {
        var world = new RestoreWorld(fixture);
        var seed = await world.SeedCentralAsync();
        var store = world.Store();
        var operation = await world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, Now.AddSeconds(10));
        var completion = failed ? Failed() : Succeeded();
        var completedAt = Now.AddSeconds(20);
        await using var context = fixture.CreatePlatformContext();
        var name = "reject_restore_" + Guid.NewGuid().ToString("N");
        await fixture.ExecuteAsync($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."TargetId" = '{operation.Id:D}' AND NEW."Action" = 'restore.completed' THEN
                    RAISE EXCEPTION 'Audit unavailable';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION {name}();
            """);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => CompleteAsync(store, claim, completedAt, completion));
            Assert.Equal(RestoreStatus.Running, (await world.OperationAsync(operation.Id)).Status);
            Assert.Equal(1, await world.ActivePinsAsync(seed.ReplicaId));
        }
        finally
        {
            await fixture.ExecuteAsync($"DROP TRIGGER {name} ON audit_events; DROP FUNCTION {name}();");
        }
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, completedAt, completion));
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(world.Store(), claim, completedAt.AddHours(1), completion));
        Assert.Equal(0, await world.ActivePinsAsync(seed.ReplicaId));
        var audit = await context.AuditEvents.AsNoTracking().SingleAsync(row => row.Action == "restore.completed" && row.TargetId == operation.Id.ToString("D"));
        Assert.Equal(seed.TenantId, audit.TenantId);
        Assert.Equal(UserId.System, audit.ActorUserId);
        Assert.Equal(AuditActorKind.System, audit.ActorKind);
        Assert.Equal(completedAt, audit.At);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(failed ? "Failed" : "Succeeded", details.RootElement.GetProperty("result").GetString());
        Assert.DoesNotContain("sql-target", audit.DetailsJson);
    }
}
