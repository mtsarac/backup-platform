using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Tenancy;

namespace BackupPlatform.UnitTests;

public sealed class TenantResolutionTests
{
    [Fact]
    public async Task ResolvesOnlyTheCurrentUsersRequestedMembership()
    {
        var userId = UserId.New();
        var otherUserId = UserId.New();
        var first = new TenantAccess(TenantId.New(), "First tenant");
        var second = new TenantAccess(TenantId.New(), "Second tenant");
        var other = new TenantAccess(TenantId.New(), "Other user's tenant");
        var store = new MemoryAccessStore();
        store.Add(userId, first);
        store.Add(userId, second);
        store.Add(otherUserId, other);
        var resolver = new TenantResolutionService(new CurrentUser(true, userId), store);
        Assert.Equal(first, await resolver.ResolveAsync(first.TenantId, CancellationToken.None));
        Assert.Equal(second, await resolver.ResolveAsync(second.TenantId, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(other.TenantId, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(TenantId.New(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task MissingAuthenticatedIdentityCannotResolveMembership(bool authenticated, bool hasUserId)
    {
        var userId = UserId.New();
        var access = new TenantAccess(TenantId.New(), "Private tenant");
        var store = new MemoryAccessStore();
        store.Add(userId, access);
        var resolver = new TenantResolutionService(new CurrentUser(authenticated, hasUserId ? userId : null), store);
        Assert.Null(await resolver.ResolveAsync(access.TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task PlatformAdminResolvesAnyTenantWithoutMembership()
    {
        var userId = UserId.New();
        var anyTenantId = TenantId.New();
        var store = new MemoryAccessStore();
        store.PlatformAdmins.Add(userId);
        store.KnownTenants.Add(anyTenantId, "Known tenant");
        var resolver = new TenantResolutionService(new CurrentUser(true, userId), store);
        var resolved = await resolver.ResolveAsync(anyTenantId, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(anyTenantId, resolved.TenantId);
        Assert.Equal("Known tenant", resolved.Name);
    }

    [Fact]
    public async Task MembershipRemovalIsVisibleOnTheNextResolution()
    {
        var userId = UserId.New();
        var access = new TenantAccess(TenantId.New(), "Tenant");
        var store = new MemoryAccessStore();
        store.Add(userId, access);
        var resolver = new TenantResolutionService(new CurrentUser(true, userId), store);
        Assert.NotNull(await resolver.ResolveAsync(access.TenantId, CancellationToken.None));
        store.Memberships.Remove((userId, access.TenantId));
        Assert.Null(await resolver.ResolveAsync(access.TenantId, CancellationToken.None));
    }

    private sealed record CurrentUser(bool IsAuthenticated, UserId? UserId) : ICurrentUser;

    private sealed class MemoryAccessStore : ICentralAuthorizationService
    {
        public Dictionary<(UserId UserId, TenantId TenantId), TenantAccess> Memberships { get; } = [];
        public Dictionary<TenantId, string> KnownTenants { get; } = [];
        public HashSet<UserId> PlatformAdmins { get; } = [];

        public void Add(UserId userId, TenantAccess access)
        {
            Memberships.Add((userId, access.TenantId), access);
            KnownTenants[access.TenantId] = access.Name;
        }

        public Task<TenantAccess?> EvaluateTenantAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken)
        {
            if (PlatformAdmins.Contains(userId))
            {
                return Task.FromResult(KnownTenants.TryGetValue(tenantId, out var name) ? new TenantAccess(tenantId, name) : null);
            }
            return Task.FromResult(Memberships.GetValueOrDefault((userId, tenantId)));
        }

        public Task<bool> EvaluateContentAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken)
        {
            if (PlatformAdmins.Contains(userId)) return Task.FromResult(false);
            return Task.FromResult(Memberships.ContainsKey((userId, tenantId)));
        }

        public Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(PlatformAdmins.Contains(userId));
    }
}
