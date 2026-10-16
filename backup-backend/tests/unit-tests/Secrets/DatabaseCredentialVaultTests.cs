using BackupPlatform.Api.Databases;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.UnitTests.Secrets;

public sealed class DatabaseCredentialVaultTests
{
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    [Fact]
    public async Task TypedLoginIsStoredUnderAReferenceOnlyItsAgentCanResolve()
    {
        var vault = new DatabaseCredentialVault(new MemorySecretStore());

        var reference = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("user_sales", "pw1!"), null, default);

        var own = await vault.ResolveAsync(AgentA, reference, default);
        Assert.Equal("user_sales", own!.UserName);
        Assert.Equal("pw1!", own.Password);
        Assert.Null(await vault.ResolveAsync(AgentB, reference, default));
    }

    [Fact]
    public async Task TwoLoginsOnTheSameAgentNeverShareAReference()
    {
        var vault = new DatabaseCredentialVault(new MemorySecretStore());

        var first = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("user_sales", "a"), null, default);
        var second = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("user_hr", "b"), null, default);

        Assert.NotEqual(first, second);
        Assert.Equal("user_hr", (await vault.ResolveAsync(AgentA, second, default))!.UserName);
    }

    [Theory]
    [InlineData("sql_sales")]
    [InlineData(null)]
    public async Task FreeTextOrMissingReferenceIsRefused(string? reference) =>
        await Assert.ThrowsAsync<DatabaseLoginRejectedException>(() => new DatabaseCredentialVault(new MemorySecretStore()).PrepareAsync(AgentA, false, null, reference, default));

    [Fact]
    public async Task AnotherAgentsReferenceIsRefused()
    {
        var vault = new DatabaseCredentialVault(new MemorySecretStore());
        var reference = await vault.PrepareAsync(AgentA, false, new DatabaseLoginInput("u", "p"), null, default);

        await Assert.ThrowsAsync<DatabaseLoginRejectedException>(() => vault.PrepareAsync(AgentB, false, null, reference, default));
    }

    [Fact]
    public async Task IntegratedSecurityNeedsNoLogin() =>
        Assert.Null(await new DatabaseCredentialVault(new MemorySecretStore()).PrepareAsync(AgentA, true, null, null, default));

    [Theory]
    [InlineData("", "p")]
    [InlineData("u", "")]
    [InlineData("u\n", "p")]
    public async Task EmptyOrControlCharacterLoginIsRefused(string user, string password) =>
        await Assert.ThrowsAsync<DatabaseLoginRejectedException>(() => new DatabaseCredentialVault(new MemorySecretStore()).StoreAsync(AgentA, new DatabaseLoginInput(user, password), default));

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
