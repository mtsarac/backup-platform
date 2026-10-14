using BackupPlatform.Domain.Identity;
using BackupPlatform.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BackupPlatform.UnitTests;

public sealed class IdentityTests
{
    [Theory]
    [InlineData(" Alice._-123 ", "ALICE._-123")]
    [InlineData("abc", "ABC")]
    [InlineData("ABC", "ABC")]
    public void NormalizationIsCanonicalAndIdempotent(string input, string expected)
    {
        var normalized = LoginIdentifierNormalizer.Normalize(input);
        Assert.Equal(expected, normalized);
        Assert.Equal(normalized, LoginIdentifierNormalizer.Normalize(normalized));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a b")]
    [InlineData("abc@example.com")]
    [InlineData("abc/def")]
    [InlineData("alice\nsmith")]
    [InlineData("álîce")]
    [InlineData("ＡＢＣ")]
    [InlineData("a\0bc")]
    public void InvalidIdentifiersAreRejected(string? input) =>
        Assert.ThrowsAny<ArgumentException>(() => LoginIdentifierNormalizer.Normalize(input!));

    [Fact]
    public void IdentifierLengthBoundaryIsAppliedAfterTrimming()
    {
        Assert.Equal(new string('A', 128), LoginIdentifierNormalizer.Normalize($" {new string('a', 128)} "));
        Assert.Throws<ArgumentException>(() => LoginIdentifierNormalizer.Normalize(new string('a', 129)));
    }

    [Fact]
    public void CredentialKeepsDisplayCasingAndRejectsInvalidMutation()
    {
        var userId = UserId.New();
        var credential = new PasswordCredential(userId, " Alice.Example ", "opaque-hash");
        Assert.Equal(userId, credential.UserId);
        Assert.Equal("Alice.Example", credential.LoginIdentifier);
        Assert.Equal("ALICE.EXAMPLE", credential.NormalizedLoginIdentifier);
        Assert.Throws<ArgumentException>(() => credential.ReplacePasswordHash(" "));
        Assert.Equal("opaque-hash", credential.PasswordHash);
        credential.ReplacePasswordHash("replacement-hash");
        Assert.Equal("replacement-hash", credential.PasswordHash);
        Assert.DoesNotContain(credential.PasswordHash, credential.ToString()!);
        Assert.Throws<ArgumentNullException>(() => new PasswordCredential(null!, "alice", "hash"));
        Assert.Throws<ArgumentException>(() => new PasswordCredential(userId, "alice", " "));
        Assert.Throws<ArgumentException>(() => new PasswordCredential(userId, "alice@example.com", "hash"));
    }

    [Fact]
    public void PasswordHashUsesRandomSaltAndRejectsWrongPassword()
    {
        var service = new PasswordHashService();
        var userId = UserId.New();
        var first = service.Hash(userId, "local-test-password");
        var second = service.Hash(userId, "local-test-password");
        Assert.NotEqual(first, second);
        Assert.Equal(PasswordVerificationResult.Success, service.Verify(userId, first, "local-test-password"));
        Assert.Equal(PasswordVerificationResult.Success, service.Verify(userId, second, "local-test-password"));
        Assert.Equal(PasswordVerificationResult.Failed, service.Verify(userId, first, "wrong-password"));
        Assert.Equal(PasswordVerificationResult.Failed, service.Verify(userId, "not-base64", "local-test-password"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPasswordsCannotBeHashedOrAuthenticated(string? password)
    {
        var service = new PasswordHashService();
        var userId = UserId.New();
        Assert.ThrowsAny<ArgumentException>(() => service.Hash(userId, password!));
        Assert.Equal(PasswordVerificationResult.Failed, service.Verify(userId, "invalid-hash", password!));
    }

    [Fact]
    public void PasswordLengthBoundaryDoesNotTruncate()
    {
        var service = new PasswordHashService();
        var userId = UserId.New();
        var password = new string('a', 1024);
        var hash = service.Hash(userId, password);
        Assert.Equal(PasswordVerificationResult.Success, service.Verify(userId, hash, password));
        Assert.Throws<ArgumentException>(() => service.Hash(userId, password + "b"));
        Assert.Equal(PasswordVerificationResult.Failed, service.Verify(userId, hash, password + "b"));
    }

    [Fact]
    public void SessionRejectsEmptyIdentitiesAndPreservesExpiry()
    {
        var userId = UserId.New();
        var expiresAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => new UserSession(Guid.Empty, userId, expiresAt));
        Assert.Throws<ArgumentNullException>(() => new UserSession(Guid.NewGuid(), null!, expiresAt));
        var id = Guid.NewGuid();
        var session = new UserSession(id, userId, expiresAt);
        Assert.Equal(id, session.Id);
        Assert.Equal(userId, session.UserId);
        Assert.Equal(expiresAt, session.ExpiresAt);
    }

    [Fact]
    public async Task LoginUpgradesOlderHashAndCreatesBoundExpiringSession()
    {
        var userId = UserId.New();
        var oldHasher = new PasswordHasher<UserId>(Options.Create(new PasswordHasherOptions { IterationCount = 10_000 }));
        var oldHash = oldHasher.HashPassword(userId, "local-password");
        var store = new MemoryIdentityStore(new PasswordCredential(userId, "Alice", oldHash));
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var hashes = new PasswordHashService();
        var authentication = new AuthenticationService(store, hashes, new FixedTimeProvider(now));
        var login = await authentication.LoginAsync(" alice ", "local-password", CancellationToken.None);
        Assert.NotNull(login);
        Assert.Equal(userId, login.UserId);
        Assert.Equal(now.AddHours(8), login.ExpiresAt);
        Assert.NotEqual(oldHash, store.Credential.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, hashes.Verify(userId, store.Credential.PasswordHash, "local-password"));
        var second = await authentication.LoginAsync("ALICE", "local-password", CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(login.SessionId, second.SessionId);
    }

    [Theory]
    [InlineData("missing", "local-password")]
    [InlineData("invalid@identifier", "local-password")]
    [InlineData(null, "local-password")]
    [InlineData("alice", "wrong-password")]
    [InlineData("alice", null)]
    [InlineData("alice", "   ")]
    public async Task FailedLoginDoesNotCreateSessionOrChangeCredential(string? identifier, string? password)
    {
        var userId = UserId.New();
        var hashes = new PasswordHashService();
        var hash = hashes.Hash(userId, "local-password");
        var store = new MemoryIdentityStore(new PasswordCredential(userId, "Alice", hash));
        var authentication = new AuthenticationService(store, hashes, TimeProvider.System);
        Assert.Null(await authentication.LoginAsync(identifier!, password!, CancellationToken.None));
        Assert.Empty(store.Sessions);
        Assert.Equal(hash, store.Credential.PasswordHash);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryIdentityStore(PasswordCredential credential) : IIdentityStore
    {
        public PasswordCredential Credential { get; } = credential;
        public Dictionary<Guid, UserSession> Sessions { get; } = [];

        public Task<PasswordCredential?> FindCredentialAsync(string normalizedLoginIdentifier, CancellationToken cancellationToken) =>
            Task.FromResult(normalizedLoginIdentifier == Credential.NormalizedLoginIdentifier ? Credential : null);

        public Task UpdatePasswordHashAsync(UserId userId, string passwordHash, CancellationToken cancellationToken)
        {
            if (userId != Credential.UserId)
            {
                throw new InvalidOperationException("Unknown user.");
            }
            Credential.ReplacePasswordHash(passwordHash);
            return Task.CompletedTask;
        }

        public Task CreateSessionAsync(UserSession session, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Sessions.Add(session.Id, session);
            return Task.CompletedTask;
        }

        public Task<bool> IsSessionActiveAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.TryGetValue(sessionId, out var session) && session.UserId == userId && session.ExpiresAt > now);

        public Task RevokeSessionAsync(Guid sessionId, UserId userId, CancellationToken cancellationToken)
        {
            if (Sessions.TryGetValue(sessionId, out var session) && session.UserId == userId)
            {
                Sessions.Remove(sessionId);
            }
            return Task.CompletedTask;
        }
        public Task RecordLoginFailureAsync(UserId? targetUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
