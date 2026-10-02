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
        var first = new TenantAccess(TenantId.New(), "First tenant", TenantRole.Viewer);
        var second = new TenantAccess(TenantId.New(), "Second tenant", TenantRole.Operator);
        var other = new TenantAccess(TenantId.New(), "Other user's tenant", TenantRole.Owner);
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
        var access = new TenantAccess(TenantId.New(), "Private tenant", TenantRole.Owner);
        var store = new MemoryAccessStore();
        store.Add(userId, access);
        var resolver = new TenantResolutionService(new CurrentUser(authenticated, hasUserId ? userId : null), store);
        Assert.Null(await resolver.ResolveAsync(access.TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task PlatformAdministratorStillNeedsMembershipAndKeepsMembershipRole()
    {
        var userId = UserId.New();
        var otherUserId = UserId.New();
        var access = new TenantAccess(TenantId.New(), "Member tenant", TenantRole.Viewer);
        var foreignAccess = new TenantAccess(TenantId.New(), "Unrelated tenant", TenantRole.Owner);
        var store = new MemoryAccessStore();
        store.PlatformAdmins.Add(userId);
        store.Add(userId, access);
        store.Add(otherUserId, foreignAccess);
        var resolver = new TenantResolutionService(new CurrentUser(true, userId), store);
        var resolved = await resolver.ResolveAsync(access.TenantId, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(TenantRole.Viewer, resolved.Role);
        Assert.Null(await resolver.ResolveAsync(foreignAccess.TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task MembershipRemovalIsVisibleOnTheNextResolution()
    {
        var userId = UserId.New();
        var access = new TenantAccess(TenantId.New(), "Tenant", TenantRole.Admin);
        var store = new MemoryAccessStore();
        store.Add(userId, access);
        var resolver = new TenantResolutionService(new CurrentUser(true, userId), store);
        Assert.NotNull(await resolver.ResolveAsync(access.TenantId, CancellationToken.None));
        store.Memberships.Remove((userId, access.TenantId));
        Assert.Null(await resolver.ResolveAsync(access.TenantId, CancellationToken.None));
    }

    private sealed record CurrentUser(bool IsAuthenticated, UserId? UserId) : ICurrentUser;

    private sealed class MemoryAccessStore : IUserAccessStore
    {
        public Dictionary<(UserId UserId, TenantId TenantId), TenantAccess> Memberships { get; } = [];
        public HashSet<UserId> PlatformAdmins { get; } = [];

        public void Add(UserId userId, TenantAccess access) => Memberships.Add((userId, access.TenantId), access);

        public Task<UserProfile?> GetUserAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<UserProfile?>(null);

        public Task<IReadOnlyList<TenantAccess>> ListTenantsAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantAccess>>(Memberships.Where(pair => pair.Key.UserId == userId).Select(pair => pair.Value).ToArray());

        public Task<TenantAccess?> GetMembershipAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Memberships.GetValueOrDefault((userId, tenantId)));

        public Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(PlatformAdmins.Contains(userId));
    }
}
