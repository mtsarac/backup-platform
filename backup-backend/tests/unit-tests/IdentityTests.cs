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
    public void SessionRejectsEmptyIdentitiesAndUsesStoredTimes()
    {
        var userId = UserId.New();
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => new UserSession(Guid.Empty, userId, now, false, "", ""));
        Assert.Throws<ArgumentNullException>(() => new UserSession(Guid.NewGuid(), null!, now, false, "", ""));
        var session = new UserSession(Guid.NewGuid(), userId, now, false, "", "");
        Assert.True(session.IsActive(now.AddHours(11)));
        Assert.False(session.IsActive(now.AddHours(12)));
        var remembered = new UserSession(Guid.NewGuid(), userId, now, true, "", "");
        Assert.True(remembered.IsActive(now.AddDays(29)));
        Assert.False(remembered.IsActive(now.AddDays(30)));
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
        var login = await authentication.LoginAsync(" alice ", "local-password", false, "", "", CancellationToken.None);
        Assert.NotNull(login);
        Assert.Equal(userId, login.UserId);
        Assert.Equal(now, store.Sessions[login.SessionId].CreatedAt);
        Assert.NotEqual(oldHash, store.Credential.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, hashes.Verify(userId, store.Credential.PasswordHash, "local-password"));
        var second = await authentication.LoginAsync("ALICE", "local-password", false, "", "", CancellationToken.None);
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
        Assert.Null(await authentication.LoginAsync(identifier!, password!, false, "", "", CancellationToken.None));
        Assert.Empty(store.Sessions);
        Assert.Equal(hash, store.Credential.PasswordHash);
    }

    [Fact]
    public async Task SaveTotpCredentialRejectsConcurrentOverwriting()
    {
        var hashes = new PasswordHashService();
        var userId = UserId.New();
        var hash = hashes.Hash(userId, "local-password");
        var store = new MemoryIdentityStore(new PasswordCredential(userId, "Alice", hash));
        var now = DateTimeOffset.UtcNow;
        var totp1 = new TotpCredential(userId, "secret1", now, null);
        var totp2 = new TotpCredential(userId, "secret2", now, null);

        var first = await store.SaveTotpCredentialAsync(totp1, ["hash1"], now, default);
        Assert.Equal(TotpEnrollmentOutcome.EnrolledWithRecoveryCodes, first);
        Assert.Equal("secret1", store.Totp?.EncryptedSecret);

        var second = await store.SaveTotpCredentialAsync(totp2, ["hash2"], now, default);
        Assert.Equal(TotpEnrollmentOutcome.Conflict, second);
        Assert.Equal("secret1", store.Totp?.EncryptedSecret);
    }

    [Fact]
    public async Task SaveChallengeCleansUpExpiredChallenges()
    {
        var hashes = new PasswordHashService();
        var userId = UserId.New();
        var hash = hashes.Hash(userId, "local-password");
        var store = new MemoryIdentityStore(new PasswordCredential(userId, "Alice", hash));
        var now = DateTimeOffset.UtcNow;

        var expired = new PasskeyChallenge(Guid.NewGuid(), userId, null, "login", "{}", now.AddMinutes(-10), now.AddMinutes(-5));
        var active = new PasskeyChallenge(Guid.NewGuid(), userId, null, "login", "{}", now.AddMinutes(-1), now.AddMinutes(4));
        await store.SaveChallengeAsync(expired, default);
        await store.SaveChallengeAsync(active, default);

        var next = new PasskeyChallenge(Guid.NewGuid(), userId, null, "login", "{}", now, now.AddMinutes(5));
        await store.SaveChallengeAsync(next, default);

        Assert.DoesNotContain(store.Challenges, c => c.Id == expired.Id);
        Assert.Contains(store.Challenges, c => c.Id == active.Id);
        Assert.Contains(store.Challenges, c => c.Id == next.Id);
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

        public Task<bool> ChangePasswordAsync(UserId userId, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (userId != Credential.UserId) return Task.FromResult(false);
            Credential.MarkPasswordChanged(newPasswordHash);
            return Task.FromResult(true);
        }
        public Task CreateSessionAsync(UserSession session, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Sessions.Add(session.Id, session);
            return Task.CompletedTask;
        }

        public Task<SessionAuthenticationState?> ValidateSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.TryGetValue(sessionId, out var session) && session.UserId == userId && session.IsActive(now) ? (SessionAuthenticationState?)session.AuthenticationState : null);
        public Task<IReadOnlyList<UserSession>> ListSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserSession>>(Sessions.Values.Where(session => session.UserId == userId && session.IsActive(now)).ToArray());
        public Task<bool> EndSessionAsync(Guid sessionId, UserId userId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.TryGetValue(sessionId, out var session) && session.UserId == userId && Sessions.Remove(sessionId));
        public Task EndAllSessionsAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            foreach (var id in Sessions.Values.Where(session => session.UserId == userId).Select(session => session.Id).ToArray()) Sessions.Remove(id);
            return Task.CompletedTask;
        }
        public Task SetSessionStateAsync(Guid sessionId, UserId userId, SessionAuthenticationState state, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordLoginFailureAsync(UserId? targetUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public TotpCredential? Totp { get; set; }
        public List<RecoveryCode> RecoveryCodes { get; } = [];

        public Task<PasswordCredential?> FindCredentialByUserIdAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(userId == Credential.UserId ? Credential : null);

        public Task<bool> HasSecondFactorAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(userId == Credential.UserId && Totp is not null);

        public Task<TotpCredential?> FindTotpCredentialAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(userId == Credential.UserId ? Totp : null);

        public Task<TotpEnrollmentOutcome> SaveTotpCredentialAsync(TotpCredential credential, IReadOnlyList<string> recoveryCodeHashes, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (Totp is not null)
            {
                return Task.FromResult(TotpEnrollmentOutcome.Conflict);
            }
            Totp = credential;
            if (recoveryCodeHashes.Count > 0)
            {
                RecoveryCodes.Clear();
                foreach (var hash in recoveryCodeHashes)
                    RecoveryCodes.Add(new RecoveryCode(Guid.NewGuid(), credential.UserId, hash, now));
                return Task.FromResult(TotpEnrollmentOutcome.EnrolledWithRecoveryCodes);
            }
            return Task.FromResult(TotpEnrollmentOutcome.EnrolledWithoutRecoveryCodes);
        }

        public Task<bool> RemoveTotpCredentialAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (Totp?.UserId == userId)
            {
                Totp = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryUseTotpStepAsync(UserId userId, long step, CancellationToken cancellationToken)
        {
            if (Totp?.UserId == userId && (Totp.LastUsedEpochStep == null || Totp.LastUsedEpochStep < step))
            {
                Totp.RecordUsedStep(step);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task SaveRecoveryCodesAsync(UserId userId, IEnumerable<string> codeHashes, DateTimeOffset now, CancellationToken cancellationToken)
        {
            RecoveryCodes.RemoveAll(c => c.UserId == userId);
            foreach (var hash in codeHashes)
                RecoveryCodes.Add(new RecoveryCode(Guid.NewGuid(), userId, hash, now));
            return Task.CompletedTask;
        }

        public Task<bool> TryRedeemRecoveryCodeAsync(UserId userId, string codeHash, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var match = RecoveryCodes.FirstOrDefault(c => c.UserId == userId && c.CodeHash == codeHash && c.RedeemedAt == null);
            if (match is not null)
            {
                match.Redeem(now);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<int> GetRemainingRecoveryCodesCountAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(RecoveryCodes.Count(c => c.UserId == userId && c.RedeemedAt == null));

        public Task<bool> HasPasskeysAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<IReadOnlyList<PasskeyCredential>> ListPasskeysAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PasskeyCredential>>([]);
        public Task<PasskeyCredential?> FindPasskeyByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken) => Task.FromResult<PasskeyCredential?>(null);
        public Task<PasskeyCredential?> FindPasskeyByIdAsync(Guid id, UserId userId, CancellationToken cancellationToken) => Task.FromResult<PasskeyCredential?>(null);
        public Task<bool> AddPasskeyAsync(PasskeyCredential passkey, IReadOnlyList<string> recoveryCodeHashes, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> RemovePasskeyAsync(Guid id, UserId userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> UpdatePasskeyUsageAsync(Guid id, uint newCounter, DateTimeOffset usedAt, CancellationToken cancellationToken) => Task.FromResult(true);
        public List<PasskeyChallenge> Challenges { get; } = [];

        public Task SaveChallengeAsync(PasskeyChallenge challenge, CancellationToken cancellationToken)
        {
            Challenges.RemoveAll(c => c.ExpiresAt <= challenge.CreatedAt);
            Challenges.Add(challenge);
            return Task.CompletedTask;
        }

        public Task<PasskeyChallenge?> ConsumeChallengeAsync(Guid challengeId, string purpose, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var match = Challenges.FirstOrDefault(c => c.Id == challengeId && c.Purpose == purpose && c.ExpiresAt > now);
            if (match is not null)
            {
                Challenges.Remove(match);
                return Task.FromResult<PasskeyChallenge?>(match);
            }
            return Task.FromResult<PasskeyChallenge?>(null);
        }
        public Task<bool> IsUserDisabledAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string?> GetUserDisplayNameAsync(UserId userId, CancellationToken cancellationToken) => Task.FromResult<string?>("Test User");
        public Task RecordRepeatedLoginFailuresAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IssueEmailTokenAsync(AccountEmailToken token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UserId?> FindUsableEmailTokenAsync(string tokenHash, AccountEmailPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ConfirmEmailAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompletePasswordResetAsync(string tokenHash, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
