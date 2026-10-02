using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

public sealed class PersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task TenantCanBeCreatedReadRenamedAndDeleted()
    {
        var tenant = new Tenant(TenantId.New(), "Original tenant");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(tenant.Id))
        {
            var persisted = await context.Tenants.SingleAsync(row => row.Id == tenant.Id);
            Assert.Equal("Original tenant", persisted.Name);
            persisted.Rename("Renamed tenant");
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(tenant.Id))
        {
            var persisted = await context.Tenants.SingleAsync(row => row.Id == tenant.Id);
            Assert.Equal("Renamed tenant", persisted.Name);
            context.Tenants.Remove(persisted);
            await context.SaveChangesAsync();
        }
        await using var verification = fixture.CreatePlatformContext();
        Assert.False(await verification.Tenants.AnyAsync(row => row.Id == tenant.Id));
    }

    [Fact]
    public async Task GlobalUserCanBePersistedRenamedAndDeletedWithoutTenantScope()
    {
        var user = new User(UserId.New(), "Original user");
        await using (var context = fixture.CreateContext((TenantId?)null))
        {
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(TenantId.New()))
        {
            var persisted = await context.Users.SingleAsync(row => row.Id == user.Id);
            Assert.Equal("Original user", persisted.DisplayName);
            persisted.Rename("Renamed user");
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext((TenantId?)null))
        {
            var persisted = await context.Users.SingleAsync(row => row.Id == user.Id);
            Assert.Equal("Renamed user", persisted.DisplayName);
            context.Users.Remove(persisted);
            await context.SaveChangesAsync();
        }
        await using var verification = fixture.CreatePlatformContext();
        Assert.False(await verification.Users.AnyAsync(row => row.Id == user.Id));
    }

    [Fact]
    public async Task MembershipRolesRemainIndependentAcrossTenantsAndPlatformGrants()
    {
        var first = await SeedAsync();
        var second = new Tenant(TenantId.New(), "Second tenant");
        await using (var platform = fixture.CreatePlatformContext())
        {
            platform.Tenants.Add(second);
            platform.TenantMemberships.Add(new TenantMembership(second.Id, first.UserId, TenantRole.Viewer));
            platform.PlatformAdmins.Add(new PlatformAdmin(first.UserId));
            await platform.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(first.TenantId))
        {
            var membership = await context.TenantMemberships.SingleAsync(row => row.UserId == first.UserId);
            Assert.Equal(TenantRole.Admin, membership.Role);
            membership.ChangeRole(TenantRole.Operator);
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(second.Id))
        {
            var membership = await context.TenantMemberships.SingleAsync(row => row.UserId == first.UserId);
            Assert.Equal(second.Id, membership.TenantId);
            Assert.Equal(TenantRole.Viewer, membership.Role);
        }
        await using (var platform = fixture.CreatePlatformContext())
        {
            var membership = await platform.TenantMemberships.SingleAsync(row => row.TenantId == first.TenantId && row.UserId == first.UserId);
            Assert.Equal(TenantRole.Operator, membership.Role);
            var grant = await platform.PlatformAdmins.SingleAsync(row => row.UserId == first.UserId);
            Assert.Equal(first.UserId, grant.UserId);
            platform.PlatformAdmins.Remove(grant);
            await platform.SaveChangesAsync();
        }
        await using var verification = fixture.CreatePlatformContext();
        Assert.False(await verification.PlatformAdmins.AnyAsync(row => row.UserId == first.UserId));
        Assert.Equal(TenantRole.Viewer, (await verification.TenantMemberships.SingleAsync(row => row.TenantId == second.Id && row.UserId == first.UserId)).Role);
    }

    [Fact]
    public async Task DuplicateMembershipIsRejectedByDatabaseAcrossContexts()
    {
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(seed.TenantId);
        context.TenantMemberships.Add(new TenantMembership(seed.TenantId, seed.UserId, TenantRole.Viewer));
        await AssertPostgresFailureAsync(context, PostgresErrorCodes.UniqueViolation);
    }

    [Theory]
    [InlineData("membership-tenant")]
    [InlineData("membership-user")]
    [InlineData("agent-tenant")]
    [InlineData("admin-user")]
    public async Task ForeignKeysRejectNonexistentPrincipals(string relationship)
    {
        var seed = await SeedAsync();
        await using var context = fixture.CreatePlatformContext();
        switch (relationship)
        {
            case "membership-tenant":
                context.TenantMemberships.Add(new TenantMembership(TenantId.New(), seed.UserId, TenantRole.Viewer));
                break;
            case "membership-user":
                context.TenantMemberships.Add(new TenantMembership(seed.TenantId, UserId.New(), TenantRole.Viewer));
                break;
            case "agent-tenant":
                context.Agents.Add(new Agent(AgentId.New(), TenantId.New(), CreateSoftware()));
                break;
            case "admin-user":
                context.PlatformAdmins.Add(new PlatformAdmin(UserId.New()));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(relationship));
        }
        await AssertPostgresFailureAsync(context, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [InlineData("membership-tenant")]
    [InlineData("membership-user")]
    [InlineData("agent-tenant")]
    [InlineData("admin-user")]
    public async Task PrincipalDeletionDoesNotCascadeToDependents(string relationship)
    {
        var tenant = new Tenant(TenantId.New(), "Restricted tenant");
        var user = new User(UserId.New(), "Restricted user");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.AddRange(tenant, user);
            if (relationship.StartsWith("membership", StringComparison.Ordinal))
            {
                context.TenantMemberships.Add(new TenantMembership(tenant.Id, user.Id, TenantRole.Viewer));
            }
            else if (relationship == "agent-tenant")
            {
                context.Agents.Add(new Agent(AgentId.New(), tenant.Id, CreateSoftware()));
            }
            else
            {
                context.PlatformAdmins.Add(new PlatformAdmin(user.Id));
            }
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreatePlatformContext())
        {
            if (relationship.EndsWith("tenant", StringComparison.Ordinal))
            {
                context.Tenants.Remove(await context.Tenants.SingleAsync(row => row.Id == tenant.Id));
            }
            else
            {
                context.Users.Remove(await context.Users.SingleAsync(row => row.Id == user.Id));
            }
            await AssertPostgresFailureAsync(context, PostgresErrorCodes.RestrictViolation);
        }
        await using var verification = fixture.CreatePlatformContext();
        Assert.True(await verification.Tenants.AnyAsync(row => row.Id == tenant.Id));
        Assert.True(await verification.Users.AnyAsync(row => row.Id == user.Id));
        if (relationship.StartsWith("membership", StringComparison.Ordinal))
        {
            Assert.True(await verification.TenantMemberships.AnyAsync(row => row.TenantId == tenant.Id && row.UserId == user.Id));
        }
        else if (relationship == "agent-tenant")
        {
            Assert.True(await verification.Agents.AnyAsync(row => row.TenantId == tenant.Id));
        }
        else
        {
            Assert.True(await verification.PlatformAdmins.AnyAsync(row => row.UserId == user.Id));
        }
    }

    [Theory]
    [InlineData(AgentPlatform.Linux)]
    [InlineData(AgentPlatform.Windows)]
    public async Task AgentSoftwareIdentityAndLifecycleSurviveRoundTrips(AgentPlatform platform)
    {
        var tenant = new Tenant(TenantId.New(), "Agent tenant");
        var software = new AgentSoftware(platform, new Version(3, 2, 1, 7), new AgentProtocolVersion(2, 4));
        var agent = new Agent(AgentId.New(), tenant.Id, software);
        agent.RecordEnrollment();
        agent.RecordCredentialRotation();
        agent.Disable();
        await using (var context = fixture.CreatePlatformContext())
        {
            context.AddRange(tenant, agent);
            await context.SaveChangesAsync();
        }
        var updatedSoftware = new AgentSoftware(platform, new Version(4, 1), new AgentProtocolVersion(3, 0));
        await using (var context = fixture.CreateContext(tenant.Id))
        {
            var persisted = await context.Agents.SingleAsync(row => row.Id == agent.Id);
            Assert.Equal(agent.Id, persisted.Id);
            Assert.Equal(tenant.Id, persisted.TenantId);
            Assert.Equal(software, persisted.Software);
            Assert.Equal(AgentStatus.Disabled, persisted.Status);
            Assert.Equal(2, persisted.CredentialGeneration);
            persisted.Enable();
            persisted.UpdateSoftware(updatedSoftware);
            persisted.RecordCredentialRotation();
            persisted.Revoke();
            await context.SaveChangesAsync();
        }
        await using var verification = fixture.CreateContext(tenant.Id);
        var reloaded = await verification.Agents.SingleAsync(row => row.Id == agent.Id);
        Assert.Equal(updatedSoftware, reloaded.Software);
        Assert.Equal(AgentStatus.Revoked, reloaded.Status);
        Assert.Equal(3, reloaded.CredentialGeneration);
    }

    [Fact]
    public async Task CachedModelKeepsTenantRootsMembershipsAndAgentsIsolatedAndFailsClosed()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using (var context = fixture.CreateContext(first.TenantId))
        {
            await AssertVisibleAsync(context, first, second);
        }
        await using (var context = fixture.CreateContext(second.TenantId))
        {
            await AssertVisibleAsync(context, second, first);
        }
        await using (var context = fixture.CreateContext((TenantId?)null))
        {
            Assert.Empty(await context.Tenants.ToListAsync());
            Assert.Empty(await context.TenantMemberships.ToListAsync());
            Assert.Empty(await context.Agents.ToListAsync());
        }
        await using (var context = fixture.CreatePlatformContext())
        {
            Assert.True(await context.Tenants.AnyAsync(row => row.Id == first.TenantId));
            Assert.True(await context.Tenants.AnyAsync(row => row.Id == second.TenantId));
            Assert.True(await context.TenantMemberships.AnyAsync(row => row.TenantId == first.TenantId && row.UserId == first.UserId));
            Assert.True(await context.TenantMemberships.AnyAsync(row => row.TenantId == second.TenantId && row.UserId == second.UserId));
            Assert.True(await context.Agents.AnyAsync(row => row.Id == first.AgentId));
            Assert.True(await context.Agents.AnyAsync(row => row.Id == second.AgentId));
        }
        await using var repeated = fixture.CreateContext(first.TenantId);
        await AssertVisibleAsync(repeated, first, second);
    }

    [Fact]
    public async Task TenantScopeIsCapturedBeforeMutableContextChanges()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        var tenantContext = new MutableTenantContext(first.TenantId);
        await using var context = fixture.CreateContext(tenantContext);
        tenantContext.TenantId = second.TenantId;
        await AssertVisibleAsync(context, first, second);
        var firstAgent = await context.Agents.SingleAsync(row => row.Id == first.AgentId);
        firstAgent.Disable();
        await context.SaveChangesAsync();
        context.Agents.Add(new Agent(AgentId.New(), second.TenantId, CreateSoftware()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        await using var verification = fixture.CreateContext(first.TenantId);
        Assert.Equal(AgentStatus.Disabled, (await verification.Agents.SingleAsync(row => row.Id == first.AgentId)).Status);
    }

    [Theory]
    [InlineData(PersistedEntity.Membership, EntityState.Added, false)]
    [InlineData(PersistedEntity.Membership, EntityState.Modified, false)]
    [InlineData(PersistedEntity.Membership, EntityState.Deleted, false)]
    [InlineData(PersistedEntity.Agent, EntityState.Added, false)]
    [InlineData(PersistedEntity.Agent, EntityState.Modified, false)]
    [InlineData(PersistedEntity.Agent, EntityState.Deleted, false)]
    [InlineData(PersistedEntity.Membership, EntityState.Added, true)]
    [InlineData(PersistedEntity.Membership, EntityState.Modified, true)]
    [InlineData(PersistedEntity.Membership, EntityState.Deleted, true)]
    [InlineData(PersistedEntity.Agent, EntityState.Added, true)]
    [InlineData(PersistedEntity.Agent, EntityState.Modified, true)]
    [InlineData(PersistedEntity.Agent, EntityState.Deleted, true)]
    [InlineData(PersistedEntity.Tenant, EntityState.Added, false)]
    [InlineData(PersistedEntity.Tenant, EntityState.Modified, false)]
    [InlineData(PersistedEntity.Tenant, EntityState.Deleted, false)]
    [InlineData(PersistedEntity.Tenant, EntityState.Added, true)]
    [InlineData(PersistedEntity.Tenant, EntityState.Modified, true)]
    [InlineData(PersistedEntity.Tenant, EntityState.Deleted, true)]
    public async Task CrossTenantAndUnscopedWritesAreRejected(PersistedEntity entity, EntityState state, bool unscoped)
    {
        var victim = await SeedAsync();
        object row;
        await using (var platform = fixture.CreatePlatformContext())
        {
            row = await LoadAsync(platform, victim, entity);
        }
        await using var context = fixture.CreateContext(unscoped ? null : TenantId.New());
        context.Entry(row).State = state;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        await using var verification = fixture.CreatePlatformContext();
        await AssertUnchangedAsync(verification, victim, entity);
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SpoofedAttachedAgentCannotWriteAnotherTenantsRowEvenWithMatchingVersion(EntityState state)
    {
        var victim = await SeedAsync();
        var attacker = await SeedAsync();
        uint version;
        await using (var platform = fixture.CreatePlatformContext())
        {
            var stored = await platform.Agents.SingleAsync(row => row.Id == victim.AgentId);
            version = platform.Entry(stored).Property<uint>("xmin").CurrentValue;
        }
        await using (var context = fixture.CreateContext(attacker.TenantId))
        {
            var spoofed = new Agent(victim.AgentId, attacker.TenantId, CreateSoftware());
            spoofed.RecordEnrollment();
            spoofed.Disable();
            var entry = context.Attach(spoofed);
            entry.Property<uint>("xmin").OriginalValue = version;
            entry.State = state;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
        }
        await using var verification = fixture.CreatePlatformContext();
        var persisted = await verification.Agents.SingleAsync(row => row.Id == victim.AgentId);
        Assert.Equal(victim.TenantId, persisted.TenantId);
        Assert.Equal(AgentStatus.Active, persisted.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackedAgentCannotBeReassignedToAnotherTenant(bool platformBypass)
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using (var context = platformBypass ? fixture.CreatePlatformContext() : fixture.CreateContext(first.TenantId))
        {
            var agent = await context.Agents.SingleAsync(row => row.Id == first.AgentId);
            // Agent.TenantId is now part of an alternate key (agent credentials reference it), so EF itself rejects
            // the reassignment before SaveChanges; the SaveChanges tenant-ownership guard remains the second layer.
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                context.Entry(agent).Property(row => row.TenantId).CurrentValue = second.TenantId;
                await context.SaveChangesAsync();
            });
        }
        await using var verification = fixture.CreatePlatformContext();
        Assert.Equal(first.TenantId, (await verification.Agents.SingleAsync(row => row.Id == first.AgentId)).TenantId);
    }

    [Theory]
    [InlineData(PersistedEntity.Tenant)]
    [InlineData(PersistedEntity.User)]
    [InlineData(PersistedEntity.Membership)]
    [InlineData(PersistedEntity.Agent)]
    public async Task StaleUpdatesAreRejectedAndDoNotOverwriteWinner(PersistedEntity entity)
    {
        var seed = await SeedAsync();
        await using var winner = fixture.CreateContext(seed.TenantId);
        await using var stale = fixture.CreateContext(seed.TenantId);
        var winnerRow = await LoadAsync(winner, seed, entity);
        var staleRow = await LoadAsync(stale, seed, entity);
        Mutate(winnerRow, "Winner");
        Mutate(staleRow, "Stale");
        await winner.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var verification = fixture.CreateContext(seed.TenantId);
        var persisted = await LoadAsync(verification, seed, entity);
        switch (persisted)
        {
            case Tenant tenant:
                Assert.Equal("Winner", tenant.Name);
                break;
            case User user:
                Assert.Equal("Winner", user.DisplayName);
                break;
            case TenantMembership membership:
                Assert.Equal(TenantRole.Operator, membership.Role);
                break;
            case Agent agent:
                Assert.Equal(AgentStatus.Disabled, agent.Status);
                break;
        }
    }

    [Fact]
    public async Task NonemptyAgentCapabilitiesAreRejectedInsteadOfSilentlyLost()
    {
        var seed = await SeedAsync();
        await using (var context = fixture.CreateContext(seed.TenantId))
        {
            var agent = await context.Agents.SingleAsync(row => row.Id == seed.AgentId);
            agent.RecordCredentialRotation();
            agent.ReplaceCapabilities(new CapabilitySet([
                new Capability(new CapabilityIdentifier("agent.snapshot"), CapabilitySupport.Supported)
            ]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }
        await using var verification = fixture.CreateContext(seed.TenantId);
        var persisted = await verification.Agents.SingleAsync(row => row.Id == seed.AgentId);
        Assert.Empty(persisted.Capabilities);
        Assert.Equal(1, persisted.CredentialGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformGrantsRequireExplicitPlatformOperations(bool unscoped)
    {
        var user = new User(UserId.New(), "Admin candidate");
        await using (var platform = fixture.CreatePlatformContext())
        {
            platform.Users.Add(user);
            await platform.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(unscoped ? null : TenantId.New()))
        {
            context.PlatformAdmins.Add(new PlatformAdmin(user.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }
        await using var verification = fixture.CreatePlatformContext();
        Assert.False(await verification.PlatformAdmins.AnyAsync(row => row.UserId == user.Id));
    }

    [Fact]
    public async Task TenantCanCreateAndRemoveItsAgentsAndMemberships()
    {
        var tenant = new Tenant(TenantId.New(), "Owned entity tenant");
        var user = new User(UserId.New(), "Owned entity user");
        var agent = new Agent(AgentId.New(), tenant.Id, CreateSoftware());
        await using (var platform = fixture.CreatePlatformContext())
        {
            platform.AddRange(tenant, user);
            await platform.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(tenant.Id))
        {
            context.Agents.Add(agent);
            context.TenantMemberships.Add(new TenantMembership(tenant.Id, user.Id, TenantRole.Viewer));
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext(tenant.Id))
        {
            var persistedAgent = await context.Agents.SingleAsync(row => row.Id == agent.Id);
            var persistedMembership = await context.TenantMemberships.SingleAsync(row => row.UserId == user.Id);
            Assert.Equal(tenant.Id, persistedAgent.TenantId);
            Assert.Equal(AgentStatus.PendingEnrollment, persistedAgent.Status);
            Assert.Equal(0, persistedAgent.CredentialGeneration);
            Assert.Equal(tenant.Id, persistedMembership.TenantId);
            Assert.Equal(TenantRole.Viewer, persistedMembership.Role);
            context.RemoveRange(persistedAgent, persistedMembership);
            await context.SaveChangesAsync();
        }
        await using var verification = fixture.CreatePlatformContext();
        Assert.False(await verification.Agents.AnyAsync(row => row.Id == agent.Id));
        Assert.False(await verification.TenantMemberships.AnyAsync(row => row.TenantId == tenant.Id && row.UserId == user.Id));
    }

    [Fact]
    public async Task InitiallyUnscopedContextCannotAcquireScopeAfterCreation()
    {
        var seed = await SeedAsync();
        var tenantContext = new MutableTenantContext(null);
        await using var context = fixture.CreateContext(tenantContext);
        tenantContext.TenantId = seed.TenantId;
        Assert.False(await context.Tenants.AnyAsync(row => row.Id == seed.TenantId));
        Assert.False(await context.TenantMemberships.AnyAsync(row => row.TenantId == seed.TenantId && row.UserId == seed.UserId));
        Assert.False(await context.Agents.AnyAsync(row => row.Id == seed.AgentId));
        context.Agents.Add(new Agent(AgentId.New(), seed.TenantId, CreateSoftware()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    private async Task<Seed> SeedAsync()
    {
        var tenant = new Tenant(TenantId.New(), "Seed tenant");
        var user = new User(UserId.New(), "Seed user");
        var membership = new TenantMembership(tenant.Id, user.Id, TenantRole.Admin);
        var agent = new Agent(AgentId.New(), tenant.Id, CreateSoftware());
        agent.RecordEnrollment();
        await using var context = fixture.CreatePlatformContext();
        context.AddRange(tenant, user, membership, agent);
        await context.SaveChangesAsync();
        return new Seed(tenant.Id, user.Id, agent.Id);
    }

    private static AgentSoftware CreateSoftware() => new(AgentPlatform.Linux, new Version(1, 2, 3), new AgentProtocolVersion(1, 2));

    private static async Task AssertVisibleAsync(PlatformDbContext context, Seed visible, Seed hidden)
    {
        Assert.Equal(visible.TenantId, (await context.Tenants.SingleAsync()).Id);
        Assert.Equal(visible.UserId, (await context.TenantMemberships.SingleAsync()).UserId);
        Assert.Equal(visible.AgentId, (await context.Agents.SingleAsync()).Id);
        Assert.False(await context.Tenants.AnyAsync(row => row.Id == hidden.TenantId));
        Assert.False(await context.TenantMemberships.AnyAsync(row => row.TenantId == hidden.TenantId && row.UserId == hidden.UserId));
        Assert.False(await context.Agents.AnyAsync(row => row.Id == hidden.AgentId));
    }

    private static async Task<object> LoadAsync(PlatformDbContext context, Seed seed, PersistedEntity entity) => entity switch
    {
        PersistedEntity.Tenant => await context.Tenants.SingleAsync(row => row.Id == seed.TenantId),
        PersistedEntity.User => await context.Users.SingleAsync(row => row.Id == seed.UserId),
        PersistedEntity.Membership => await context.TenantMemberships.SingleAsync(row => row.TenantId == seed.TenantId && row.UserId == seed.UserId),
        PersistedEntity.Agent => await context.Agents.SingleAsync(row => row.Id == seed.AgentId),
        _ => throw new ArgumentOutOfRangeException(nameof(entity))
    };

    private static async Task AssertUnchangedAsync(PlatformDbContext context, Seed seed, PersistedEntity entity)
    {
        var persisted = await LoadAsync(context, seed, entity);
        switch (persisted)
        {
            case Tenant tenant:
                Assert.Equal("Seed tenant", tenant.Name);
                break;
            case TenantMembership membership:
                Assert.Equal(TenantRole.Admin, membership.Role);
                break;
            case Agent agent:
                Assert.Equal(AgentStatus.Active, agent.Status);
                Assert.Equal(seed.TenantId, agent.TenantId);
                break;
        }
    }

    private static void Mutate(object row, string name)
    {
        switch (row)
        {
            case Tenant tenant:
                tenant.Rename(name);
                break;
            case User user:
                user.Rename(name);
                break;
            case TenantMembership membership:
                membership.ChangeRole(name == "Winner" ? TenantRole.Operator : TenantRole.Viewer);
                break;
            case Agent agent:
                if (name == "Winner")
                {
                    agent.Disable();
                }
                else
                {
                    agent.RecordCredentialRotation();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row));
        }
    }

    private static async Task AssertPostgresFailureAsync(PlatformDbContext context, string sqlState)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    private sealed record Seed(TenantId TenantId, UserId UserId, AgentId AgentId);

    public enum PersistedEntity
    {
        Tenant,
        User,
        Membership,
        Agent
    }
}
