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
        Assert.Throws<ArgumentNullException>(() => new TenantMembership(default!, UserId.New()));
        Assert.Throws<ArgumentNullException>(() => new TenantMembership(TenantId.New(), default!));
    }

    [Fact]
    public void UserHasRoleAndMembershipsAreRoleless()
    {
        var userAdmin = new User(UserId.New(), "Alice", UserRole.UserAdmin);
        var platformAdmin = new User(UserId.New(), "Bob", UserRole.PlatformAdmin);
        var first = new TenantMembership(TenantId.New(), userAdmin.Id);
        var second = new TenantMembership(TenantId.New(), userAdmin.Id);
        Assert.Equal(UserRole.UserAdmin, userAdmin.Role);
        Assert.Equal(UserRole.PlatformAdmin, platformAdmin.Role);
        Assert.Equal(userAdmin.Id, first.UserId);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new User(userId, "Alice", (UserRole)value));
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
