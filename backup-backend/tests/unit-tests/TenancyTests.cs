using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.UnitTests;

public sealed class TenancyTests
{
    [Fact]
    public void IdsRejectEmptyAndCompareByValue()
    {
        Assert.Throws<ArgumentException>(() => new TenantId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new UserId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new AgentId(Guid.Empty));
        var value = Guid.NewGuid();
        Assert.Equal(new TenantId(value), new TenantId(value));
        Assert.Equal(new UserId(value), new UserId(value));
        Assert.Equal(new AgentId(value), new AgentId(value));
        Assert.NotEqual<object>(new TenantId(value), new UserId(value));
    }

    [Fact]
    public void DefaultIdsCannotEnterEntities()
    {
        Assert.Throws<ArgumentNullException>(() => new Tenant(default!, "Tenant"));
        Assert.Throws<ArgumentNullException>(() => new User(default!, "User"));
        Assert.Throws<ArgumentNullException>(() => new TenantMembership(default!, UserId.New(), TenantRole.Admin));
        Assert.Throws<ArgumentNullException>(() => new TenantMembership(TenantId.New(), default!, TenantRole.Admin));
        Assert.Throws<ArgumentNullException>(() => new PlatformAdmin(default!));
    }

    [Fact]
    public void MembershipRolesAreIndependentAcrossTenantsAndFromPlatformGrant()
    {
        var user = new User(UserId.New(), "Alice");
        var first = new TenantMembership(TenantId.New(), user.Id, TenantRole.Admin);
        var second = new TenantMembership(TenantId.New(), user.Id, TenantRole.Viewer);
        var platformAdmin = new PlatformAdmin(user.Id);
        first.ChangeRole(TenantRole.Operator);
        Assert.Equal(TenantRole.Operator, first.Role);
        Assert.Equal(TenantRole.Viewer, second.Role);
        Assert.Equal(user.Id, platformAdmin.UserId);
        Assert.NotEqual(first.TenantId, second.TenantId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public void InvalidRoleCannotBeAssigned(int value)
    {
        var tenantId = TenantId.New();
        var userId = UserId.New();
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantMembership(tenantId, userId, (TenantRole)value));
        var membership = new TenantMembership(tenantId, userId, TenantRole.Viewer);
        Assert.Throws<ArgumentOutOfRangeException>(() => membership.ChangeRole((TenantRole)value));
        Assert.Equal(TenantRole.Viewer, membership.Role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NamesCannotBeBlankOrInvalidateExistingState(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Tenant(TenantId.New(), name!));
        Assert.ThrowsAny<ArgumentException>(() => new User(UserId.New(), name!));
        var tenant = new Tenant(TenantId.New(), "Tenant");
        var user = new User(UserId.New(), "Alice");
        Assert.ThrowsAny<ArgumentException>(() => tenant.Rename(name!));
        Assert.ThrowsAny<ArgumentException>(() => user.Rename(name!));
        Assert.Equal("Tenant", tenant.Name);
        Assert.Equal("Alice", user.DisplayName);
    }
}
