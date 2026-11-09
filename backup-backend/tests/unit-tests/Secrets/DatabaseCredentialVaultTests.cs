using BackupPlatform.Api.Databases;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.UnitTests.Secrets;

public sealed class DatabaseCredentialVaultTests
{
    private static DatabaseCredentialVault NewVault(MemorySecretStore store, NoClaims? claims = null) => new(store, claims ?? new NoClaims(), TimeProvider.System);

    private sealed class NoClaims : BackupPlatform.Application.Backups.IJobCredentialClaims
    {
        public List<string> Claimed { get; } = [];
        public List<string> Released { get; } = [];
        public Task ClaimAsync(string reference, DateTimeOffset now, CancellationToken cancellationToken) { Claimed.Add(reference); return Task.CompletedTask; }
        public Task ReleaseAsync(string reference, CancellationToken cancellationToken) { Released.Add(reference); return Task.CompletedTask; }
        public Task<int> SweepStaleAsync(DateTimeOffset olderThan, int limit, Func<string, CancellationToken, Task> deleteCredential, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    [Fact]
    public async Task A_seeded_login_is_claimed_before_it_is_stored_gets_the_same_reference_every_time_and_discarding_it_releases_the_claim()
    {
        var store = new MemorySecretStore(); var claims = new NoClaims(); var vault = NewVault(store, claims);
        var first = await vault.StoreAsync(AgentA, new DatabaseLoginInput("sa", "pw"), default, "seed-1");
        var again = await vault.StoreAsync(AgentA, new DatabaseLoginInput("sa", "pw"), default, "seed-1");
        Assert.Equal(first, again);
        Assert.NotEqual(first, await vault.StoreAsync(AgentA, new DatabaseLoginInput("sa", "pw"), default, "seed-2"));
        Assert.Equal([first, first, claims.Claimed[2]], claims.Claimed);
        Assert.True(DatabaseCredentialVault.BelongsTo(first, AgentA));
        Assert.True(await vault.DeleteAsync(first, default));
        Assert.Contains(first, claims.Released);
        Assert.Null(await vault.ResolveAsync(AgentA, first, default));
    }

    [Fact]
    public async Task An_unseeded_login_is_never_claimed()
    {
        var claims = new NoClaims();
        await NewVault(new MemorySecretStore(), claims).StoreAsync(AgentA, new DatabaseLoginInput("sa", "pw"), default);
        Assert.Empty(claims.Claimed);
    }

    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    [Fact]
    public async Task TypedLoginIsStoredUnderAReferenceOnlyItsAgentCanResolve()
    {
        var vault = NewVault(new MemorySecretStore());

        var reference = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("user_sales", "pw1!"), null, default);

        var own = await vault.ResolveAsync(AgentA, reference, default);
        Assert.Equal("user_sales", own!.UserName);
        Assert.Equal("pw1!", own.Password);
        Assert.Null(await vault.ResolveAsync(AgentB, reference, default));
    }

    [Fact]
    public async Task TwoLoginsOnTheSameAgentNeverShareAReference()
    {
        var vault = NewVault(new MemorySecretStore());

        var first = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("user_sales", "a"), null, default);
        var second = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("user_hr", "b"), null, default);

        Assert.NotEqual(first, second);
        Assert.Equal("user_hr", (await vault.ResolveAsync(AgentA, second, default))!.UserName);
    }

    [Theory]
    [InlineData("sql_sales")]
    [InlineData(null)]
    public async Task FreeTextOrMissingReferenceIsRefused(string? reference) =>
        await Assert.ThrowsAsync<DatabaseLoginRejectedException>(() => NewVault(new MemorySecretStore()).PrepareAsync(AgentA, false, null, reference, default));

    [Fact]
    public async Task AnotherAgentsReferenceIsRefused()
    {
        var vault = NewVault(new MemorySecretStore());
        var reference = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("u", "p"), null, default);

        await Assert.ThrowsAsync<DatabaseLoginRejectedException>(() => vault.PrepareAsync(AgentB, false, null, reference, default));
    }

    [Fact]
    public async Task IntegratedSecurityNeedsNoLogin() =>
        Assert.Null(await NewVault(new MemorySecretStore()).PrepareAsync(AgentA, true, null, null, default));

    [Theory]
    [InlineData("", "p")]
    [InlineData("u", "")]
    [InlineData("u\n", "p")]
    public async Task EmptyOrControlCharacterLoginIsRefused(string user, string password) =>
        await Assert.ThrowsAsync<DatabaseLoginRejectedException>(() => NewVault(new MemorySecretStore()).StoreAsync(AgentA, new DatabaseLoginInput(user, password), default));

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, SecretValue> _values = [];
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
            _values.TryGetValue(reference.Name, out var value) ? ValueTask.FromResult(value) : throw new SecretUnavailableException();
        public Task SetAsync(SecretReference reference, SecretValue value, CancellationToken cancellationToken) { _values[reference.Name] = value; return Task.CompletedTask; }
        public Task<bool> DeleteAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(_values.Remove(reference.Name));
        public Task<bool> ExistsAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(_values.ContainsKey(reference.Name));
    }
}
