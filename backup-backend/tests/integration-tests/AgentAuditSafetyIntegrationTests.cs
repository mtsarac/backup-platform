using BackupPlatform.Agents.Credentials;
using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Agents;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentAuditSafetyIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task RefusedConfirmationAuditRestoresBothCredentialsAndGeneration()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        var store = new PostgreSqlAgentRegistryStore(fixture.Options);
        var identity = new AgentIdentity(new AgentId(agent.AgentId), setup.Tenant.Id, agent.CredentialId, 1);
        var now = world.Clock.GetUtcNow();
        var started = await store.BeginRotationAsync(identity, now, TimeSpan.FromHours(1), default);
        var pendingId = started.Issued!.Credential.Id;
        var pending = identity with { CredentialId = pendingId, CredentialGeneration = 2 };
        await using var context = fixture.CreatePlatformContext();
        var name = "reject_rotation_" + Guid.NewGuid().ToString("N");
        await fixture.ExecuteAsync($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."TargetId" = '{agent.AgentId:D}' AND NEW."Action" = 'agent.credential_rotation_confirmed' THEN
                    RAISE EXCEPTION 'Audit unavailable';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION {name}();
            """);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => store.ConfirmRotationAsync(pending, now, default));
            Assert.Equal(AgentCredentialStatus.Active, (await store.FindCredentialAsync(agent.CredentialId, default))!.Credential.Status);
            Assert.Equal(AgentCredentialStatus.Pending, (await store.FindCredentialAsync(pendingId, default))!.Credential.Status);
            Assert.Equal(1, (await context.Agents.AsNoTracking().SingleAsync(row => row.Id == identity.AgentId)).CredentialGeneration);
        }
        finally
        {
            await fixture.ExecuteAsync($"DROP TRIGGER {name} ON audit_events; DROP FUNCTION {name}();");
        }
        await Task.WhenAll(store.ConfirmRotationAsync(pending, now, default), store.ConfirmRotationAsync(pending, now, default));
        Assert.Equal(AgentCredentialStatus.Revoked, (await store.FindCredentialAsync(agent.CredentialId, default))!.Credential.Status);
        Assert.Equal(AgentCredentialStatus.Active, (await store.FindCredentialAsync(pendingId, default))!.Credential.Status);
        var audit = await context.AuditEvents.AsNoTracking().SingleAsync(row => row.Action == AuditActions.AgentCredentialRotationConfirmed && row.TargetId == agent.AgentId.ToString("D"));
        Assert.Equal(setup.Tenant.Id, audit.TenantId);
        Assert.Equal(AuditActorKind.System, audit.ActorKind);
        Assert.DoesNotContain(started.Issued.Secret, audit.DetailsJson);
    }
}
