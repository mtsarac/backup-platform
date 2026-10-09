using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Api.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Secrets;
using BackupPlatform.Infrastructure.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.SecretProviders.DataProtection;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class GoogleDriveOAuthHardeningTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private DbContextOptions<PlatformDbContext> DbOptions => new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options;

    private static readonly IOptions<GoogleDrivePlatformOptions> PlatformOptions = Options.Create(new GoogleDrivePlatformOptions
    {
        ClientId = "test-client", ClientSecret = "test-secret",
        RedirectUri = "https://backup.example.test/api/platform/google-drive/oauth/callback"
    });

    [Fact]
    public async Task IndependentFactoriesSerializeRefreshAndReadTheCommittedRotation()
    {
        var connection = await SeedAsync();
        var originalReference = $"sc-{connection.Id:N}-initial";
        await using (var dbInit = fixture.CreatePlatformContext())
        {
            var entity = await dbInit.StorageConnections.SingleAsync(row => row.Id == connection.Id);
            entity.AttachSecret(originalReference, ["refreshToken"], DateTimeOffset.UtcNow);
            await dbInit.SaveChangesAsync();
        }
        connection.AttachSecret(originalReference, ["refreshToken"], DateTimeOffset.UtcNow);
        var protection = new EphemeralDataProtectionProvider();
        var secrets = new DataProtectionSecretStore(protection, new PostgreSqlSecretCiphertextRepository(DbOptions, TimeProvider.System));
        var otherNodeSecrets = new DataProtectionSecretStore(protection, new PostgreSqlSecretCiphertextRepository(DbOptions, TimeProvider.System));
        await secrets.SetAsync(new SecretReference(connection.SecretReference!), new SecretValue("{\"refreshToken\":\"initial\"}"), default);
        using var handler = new TokenHandler();
        using var http = new HttpClient(handler);
        var first = Factory(http, secrets).Create(connection, secrets);
        var second = Factory(http, otherNodeSecrets).Create(connection, otherNodeSecrets);
        var key = new StorageObjectKey($"{connection.TenantId.Value:N}/missing.bak");

        await Task.WhenAll(first.ExistsAsync(key, default), second.ExistsAsync(key, default));

        Assert.Equal(1, handler.MaxConcurrentRefreshes);
        Assert.Contains(handler.RefreshTokens, token => token == "initial");
        await using var db = fixture.CreatePlatformContext();
        var current = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.NotEqual(originalReference, current.SecretReference);
        Assert.False(await secrets.ExistsAsync(new SecretReference(originalReference), default));
        var persisted = await secrets.ResolveAsync(new SecretReference(current.SecretReference!), default);
        var tokenValue = JsonSerializer.Deserialize<Dictionary<string, string>>(persisted.Reveal())!["refreshToken"];
        Assert.StartsWith("rotated-", tokenValue);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task OnlyRevokedGrantMarksServerStateAndConnectionTestReportsTheFailure(bool invalidGrant, string expected)
    {
        var connection = await SeedAsync();
        var secrets = new MemorySecrets();
        await secrets.SetAsync(new SecretReference(connection.SecretReference!), new SecretValue("{\"refreshToken\":\"initial\"}"), default);
        using var handler = new TokenHandler { TokenError = invalidGrant ? "invalid_grant" : "invalid_client" };
        using var http = new HttpClient(handler);
        var factory = Factory(http, secrets);
        var service = Service(secrets, [new GoogleDriveConnectionTester(factory.Create)]);

        var result = await service.TestAsync(StorageScope.Tenant(connection.TenantId, canManage: true), connection.Id, UserId.New(), default);

        Assert.True(result.Succeeded);
        Assert.Equal(StorageTestCodes.AuthenticationFailed, result.Value!.Test.Code);
        Assert.Equal(expected, result.Value.Connection.Configuration["reauthorizationRequired"]);
        await using var db = fixture.CreatePlatformContext();
        var persisted = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Equal(expected, persisted.Configuration["reauthorizationRequired"]);
    }

    [Fact]
    public async Task ReauthorizationFlagCannotBeSubmittedAndOrdinaryUpdatesPreserveIt()
    {
        var connection = await SeedAsync(reauthorizationRequired: "true");
        var service = Service(new MemorySecrets(), []);
        var scope = StorageScope.Tenant(connection.TenantId, canManage: true);
        var actor = UserId.New();
        await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => service.UpdateAsync(scope, connection.Id,
            new UpdateStorageConnectionCommand(null, null, new Dictionary<string, string> { ["reauthorizationRequired"] = "false" }), actor, default));
        var result = await service.UpdateAsync(scope, connection.Id,
            new UpdateStorageConnectionCommand(null, null, new Dictionary<string, string> { ["folderId"] = "another-folder" }), actor, default);
        Assert.True(result.Succeeded);
        Assert.Equal("true", result.Value!.Configuration["reauthorizationRequired"]);

        var connected = await SeedAsync();
        var clear = await service.ClearSecretAsync(StorageScope.Tenant(connected.TenantId, canManage: true), connected.Id, actor, default);
        Assert.True(clear.Succeeded);
        Assert.Equal("true", clear.Value!.Configuration["reauthorizationRequired"]);
        Assert.False(clear.Value.HasSecret);
    }

    [Fact]
    public async Task ClearingAnAlreadyAbsentSecretStillPersistsReauthorizationState()
    {
        var connection = await SeedAsync();
        await using (var db = fixture.CreatePlatformContext())
        {
            var persisted = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
            persisted.DetachSecret(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        var result = await Service(new MemorySecrets(), []).ClearSecretAsync(
            StorageScope.Tenant(connection.TenantId, canManage: true), connection.Id, UserId.New(), default);
        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal("true", result.Value!.Configuration["reauthorizationRequired"]);
        await using var verification = fixture.CreatePlatformContext();
        Assert.Equal("true", (await verification.StorageConnections.SingleAsync(row => row.Id == connection.Id)).Configuration["reauthorizationRequired"]);
    }

    [Fact]
    public async Task ConcurrentCallbacksConsumeStateOnce()
    {
        var connection = await SeedAsync();
        var secrets = new MemorySecrets();
        using var handler = new TokenHandler();
        using var http = new HttpClient(handler);
        var first = OAuth(http, secrets);
        var second = OAuth(http, secrets);
        var authorization = await first.CreateAuthorizationUrlAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorization).Query)["state"]!;

        var results = await Task.WhenAll(first.ProcessCallbackAsync("code", state, null, default), second.ProcessCallbackAsync("code", state, null, default));

        Assert.Single(results, result => result.Success);
        Assert.Equal(1, handler.AuthorizationExchanges);
        await using var db = fixture.CreatePlatformContext();
        Assert.True((await db.GoogleDriveOAuthStates.SingleAsync(row => row.State == state)).Used);
        Assert.Equal("false", (await db.StorageConnections.SingleAsync(row => row.Id == connection.Id)).Configuration["reauthorizationRequired"]);
    }

    [Fact]
    public async Task CallbackCannotReconnectAnAdministrativelyDisabledConnection()
    {
        var connection = await SeedAsync();
        using var handler = new TokenHandler();
        using var http = new HttpClient(handler);
        var oauth = OAuth(http, new MemorySecrets());
        var authorization = await oauth.CreateAuthorizationUrlAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorization).Query)["state"]!;
        await using (var db = fixture.CreatePlatformContext())
        {
            var persisted = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
            persisted.SetAdministrativelyDisabled(true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var result = await oauth.ProcessCallbackAsync("code", state, null, default);

        Assert.False(result.Success);
        Assert.Equal(0, handler.AuthorizationExchanges);
        await using var verification = fixture.CreatePlatformContext();
        Assert.Equal(connection.SecretReference, (await verification.StorageConnections.SingleAsync(row => row.Id == connection.Id)).SecretReference);
    }

    [Fact]
    public async Task MissingNewRefreshTokenDoesNotRelabelAnExistingGrant()
    {
        var connection = await SeedAsync(reauthorizationRequired: "true");
        var secrets = new MemorySecrets();
        var oldReference = connection.SecretReference!;
        await secrets.SetAsync(new SecretReference(oldReference), new SecretValue("{\"refreshToken\":\"old-account-token\"}"), default);
        using var handler = new TokenHandler { OmitAuthorizationRefreshToken = true };
        using var http = new HttpClient(handler);
        var oauth = OAuth(http, secrets);
        var authorization = await oauth.CreateAuthorizationUrlAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorization).Query)["state"]!;

        var result = await oauth.ProcessCallbackAsync("code", state, null, default);

        Assert.False(result.Success);
        await using var db = fixture.CreatePlatformContext();
        var persisted = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Equal(oldReference, persisted.SecretReference);
        Assert.Equal("true", persisted.Configuration["reauthorizationRequired"]);
        Assert.False(persisted.Configuration.ContainsKey("accountEmail"));
    }

    [Fact]
    public void GoogleDriveMigrationTargetMatchesSnapshotAndUpgradeIsAdditive()
    {
        using var db = fixture.CreatePlatformContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[assembly.Migrations.Last().Key], db.Database.ProviderName!);
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var snapshot = initializer.Initialize(assembly.ModelSnapshot!.Model, designTime: true);
        Assert.False(db.GetService<IMigrationsModelDiffer>().HasDifferences(snapshot.GetRelationalModel(), target.GetRelationalModel()));
        var configured = db.GetService<IDesignTimeModel>().Model;
        Assert.False(db.GetService<IMigrationsModelDiffer>().HasDifferences(target.GetRelationalModel(), configured.GetRelationalModel()));
        Assert.Contains(target.GetEntityTypes(), entity => entity.GetTableName() == "google_drive_oauth_states");
        Assert.Contains(target.GetEntityTypes(), entity => entity.GetTableName() == "google_drive_file_metadata");
        var script = db.GetService<IMigrator>().GenerateScript("20261009140000_BackendRelayedRestoreSources", "20261009160000_GoogleDriveStorageProvider");
        Assert.Contains("CREATE TABLE google_drive_oauth_states", script);
        Assert.Contains("CREATE TABLE google_drive_file_metadata", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("ALTER TABLE storage_connections", script);
    }

    [Fact]
    public async Task TenantMutationDoesNotWaitForRefreshHttp()
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseFirstRefresh = true };
        using var http = new HttpClient(handler);
        var refresh = Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var result = await Service(secrets, []).UpdateAsync(StorageScope.Tenant(connection.TenantId, canManage: true), connection.Id,
                new UpdateStorageConnectionCommand("Renamed while refreshing", null, null), UserId.New(), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Succeeded);
            Assert.False(refresh.IsCompleted);
        }
        finally { handler.ResumeRefresh.TrySetResult(); }
        await refresh;
        await using var db = fixture.CreatePlatformContext();
        Assert.Equal("Renamed while refreshing", (await db.StorageConnections.SingleAsync(row => row.Id == connection.Id)).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid_grant")]
    public async Task ARefreshCannotOverwriteOrRevokeNewerGenericCredentials(string? tokenError)
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseFirstRefresh = true, TokenError = tokenError };
        using var http = new HttpClient(handler);
        var refresh = Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string newerReference;
        try
        {
            var replacement = await Service(secrets, []).SetSecretAsync(StorageScope.Tenant(connection.TenantId, canManage: true), connection.Id,
                new Dictionary<string, string> { ["refreshToken"] = "new-authorization" }, UserId.New(), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(replacement.Succeeded);
            await using var db = fixture.CreatePlatformContext();
            newerReference = (await db.StorageConnections.SingleAsync(row => row.Id == connection.Id)).SecretReference!;
        }
        finally { handler.ResumeRefresh.TrySetResult(); }
        var failure = await Assert.ThrowsAsync<StorageException>(() => refresh);
        Assert.Equal(StorageFailureKind.AuthenticationFailed, failure.Kind);
        await using var verification = fixture.CreatePlatformContext();
        var current = await verification.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Equal(newerReference, current.SecretReference);
        Assert.Equal("false", current.Configuration["reauthorizationRequired"]);
        Assert.Equal("new-authorization", await RefreshTokenAsync(secrets, current.SecretReference!));
        Assert.Equal(1, secrets.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid_grant")]
    public async Task AReusedSecretReferenceIsAlsoFencedByItsCredentialValue(string? tokenError)
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseFirstRefresh = true, TokenError = tokenError };
        using var http = new HttpClient(handler);
        var refresh = Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await secrets.SetAsync(new SecretReference(connection.SecretReference!), new SecretValue("{\"refreshToken\":\"new-in-place\"}"), default);
        }
        finally { handler.ResumeRefresh.TrySetResult(); }
        Assert.Equal(StorageFailureKind.AuthenticationFailed, (await Assert.ThrowsAsync<StorageException>(() => refresh)).Kind);
        await using var db = fixture.CreatePlatformContext();
        var current = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Equal(connection.SecretReference, current.SecretReference);
        Assert.Equal("false", current.Configuration["reauthorizationRequired"]);
        Assert.Equal("new-in-place", await RefreshTokenAsync(secrets, current.SecretReference!));
        Assert.Equal(1, secrets.Count);
    }

    [Fact]
    public async Task ReauthorizationCannotBeOverwrittenByAStalledRefresh()
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseFirstRefresh = true };
        using var http = new HttpClient(handler);
        var refresh = Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var oauth = OAuth(http, secrets);
            var authorization = await oauth.CreateAuthorizationUrlAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default);
            var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorization).Query)["state"]!;
            Assert.True((await oauth.ProcessCallbackAsync("code", state, null, default).WaitAsync(TimeSpan.FromSeconds(5))).Success);
        }
        finally { handler.ResumeRefresh.TrySetResult(); }
        Assert.Equal(StorageFailureKind.AuthenticationFailed, (await Assert.ThrowsAsync<StorageException>(() => refresh)).Kind);
        await using var db = fixture.CreatePlatformContext();
        var current = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Equal("authorized", await RefreshTokenAsync(secrets, current.SecretReference!));
        Assert.Equal("false", current.Configuration["reauthorizationRequired"]);
        Assert.Equal(1, secrets.Count);
    }

    [Fact]
    public async Task ARefreshCannotReattachCredentialsAfterDisconnect()
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseFirstRefresh = true };
        using var http = new HttpClient(handler);
        var refresh = Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await OAuth(http, secrets).DisconnectAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { handler.ResumeRefresh.TrySetResult(); }
        Assert.Equal(StorageFailureKind.AuthenticationFailed, (await Assert.ThrowsAsync<StorageException>(() => refresh)).Kind);
        await using var db = fixture.CreatePlatformContext();
        var current = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Null(current.SecretReference);
        Assert.Equal("true", current.Configuration["reauthorizationRequired"]);
        Assert.Equal(0, secrets.Count);
    }

    [Fact]
    public async Task CallbackHttpDoesNotHoldTenantLockAndCannotUndoDisconnect()
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseAuthorization = true };
        using var http = new HttpClient(handler);
        var oauth = OAuth(http, secrets);
        var authorization = await oauth.CreateAuthorizationUrlAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorization).Query)["state"]!;
        var callback = oauth.ProcessCallbackAsync("code", state, null, default);
        await handler.AuthorizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await OAuth(http, secrets).DisconnectAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(callback.IsCompleted);
        }
        finally { handler.ResumeAuthorization.TrySetResult(); }
        Assert.False((await callback).Success);
        await using var db = fixture.CreatePlatformContext();
        var current = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Null(current.SecretReference);
        Assert.Equal("true", current.Configuration["reauthorizationRequired"]);
        Assert.Equal(0, secrets.Count);
    }

    [Fact]
    public async Task DisconnectCommitsBeforeRevocationHttpWithoutBlockingTenantMutation()
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseRevocation = true };
        using var http = new HttpClient(handler);
        var disconnect = OAuth(http, secrets).DisconnectAsync(connection.TenantId, connection.Id, Guid.NewGuid(), default);
        await handler.RevocationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var mutation = await Service(secrets, []).UpdateAsync(StorageScope.Tenant(connection.TenantId, canManage: true), connection.Id,
                new UpdateStorageConnectionCommand("Renamed while revoking", null, null), UserId.New(), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(mutation.Succeeded);
            Assert.False(mutation.Value!.HasSecret);
            Assert.False(disconnect.IsCompleted);
        }
        finally { handler.ResumeRevocation.TrySetResult(); }
        await disconnect;
        Assert.Equal(0, secrets.Count);
    }

    [Fact]
    public async Task TerminatedRefreshSessionReleasesLockAndCannotCommitItsRotation()
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseFirstRefresh = true };
        using var http = new HttpClient(handler);
        var first = Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var control = new NpgsqlConnection(fixture.ConnectionString);
            await control.OpenAsync();
            await using var terminate = new NpgsqlCommand("""
                SELECT pg_terminate_backend(pid) FROM pg_stat_activity
                WHERE datname = current_database() AND application_name = 'BackupPlatform.GoogleDrive.Refresh'
                """, control);
            Assert.Equal(true, await terminate.ExecuteScalarAsync());
            await Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { handler.ResumeRefresh.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        await using var db = fixture.CreatePlatformContext();
        var current = await db.StorageConnections.SingleAsync(row => row.Id == connection.Id);
        Assert.Equal("rotated-1", await RefreshTokenAsync(secrets, current.SecretReference!));
        Assert.Equal(1, secrets.Count);
    }

    [Fact]
    public async Task WaitingForAnotherNodeHasABoundedDeadlineAndHonorsCancellation()
    {
        var connection = await SeedAsync();
        var secrets = await InitialSecretsAsync(connection);
        using var handler = new TokenHandler { PauseFirstRefresh = true };
        using var http = new HttpClient(handler);
        var first = Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), default);
        await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var limited = Options.Create(new GoogleDrivePlatformOptions
            {
                ClientId = "test-client", ClientSecret = "test-secret", RetryBudget = TimeSpan.FromMilliseconds(150)
            });
            var failure = await Assert.ThrowsAsync<StorageException>(() =>
                Factory(http, secrets, limited).Create(connection, secrets).ExistsAsync(Key(connection), default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Factory(http, secrets).Create(connection, secrets).ExistsAsync(Key(connection), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { handler.ResumeRefresh.TrySetResult(); }
        await first;
        Assert.Single(handler.RefreshTokens);
    }

    private static StorageObjectKey Key(StorageConnection connection) => new($"{connection.TenantId.Value:N}/missing.bak");

    private static async Task<MemorySecrets> InitialSecretsAsync(StorageConnection connection)
    {
        var secrets = new MemorySecrets();
        await secrets.SetAsync(new SecretReference(connection.SecretReference!), new SecretValue("{\"refreshToken\":\"initial\"}"), default);
        return secrets;
    }

    private static async Task<string> RefreshTokenAsync(ISecretProvider secrets, string reference) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>((await secrets.ResolveAsync(new SecretReference(reference), default)).Reveal())!["refreshToken"];

    private async Task<StorageConnection> SeedAsync(string reauthorizationRequired = "false")
    {
        var tenant = new Tenant(TenantId.New(), "OAuth Hardening");
        var connection = new StorageConnection(Guid.NewGuid(), tenant.Id, "Drive", StorageProviderTypes.GoogleDrive,
            StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>
            {
                ["folderId"] = "test-folder", ["reauthorizationRequired"] = reauthorizationRequired
            }, isDefault: false, DateTimeOffset.UtcNow);
        connection.AttachSecret($"sc-{connection.Id:N}-{Guid.NewGuid():N}", ["refreshToken"], DateTimeOffset.UtcNow);
        await using var db = fixture.CreatePlatformContext();
        db.Tenants.Add(tenant);
        db.StorageConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private GoogleDriveConnectionProviderFactory Factory(HttpClient http, ISecretStore secrets, IOptions<GoogleDrivePlatformOptions>? options = null, IGoogleDriveStagingStore? staging = null) =>
        new(http, options ?? PlatformOptions, new InMemoryGoogleDriveFileCatalog(), secrets, DbOptions, stagingStore: staging ?? new EphemeralStagingStore());
    private GoogleDriveOAuthService OAuth(HttpClient http, ISecretStore secrets) =>
        new(http, PlatformOptions, DbOptions, secrets, TimeProvider.System, NullLogger<GoogleDriveOAuthService>.Instance);

    private StorageConnectionService Service(ISecretStore secrets, IEnumerable<IStorageConnectionTester> testers) =>
        new(new PostgreSqlStorageConnectionStore(DbOptions, TimeProvider.System, NullLogger<PostgreSqlStorageConnectionStore>.Instance),
            StorageProviderCatalog.BuiltIn(), secrets, testers, TimeProvider.System, NullLogger<StorageConnectionService>.Instance);

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public int Count => _values.Count;
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SecretValue(_values[reference.Name]));
        public Task SetAsync(SecretReference reference, SecretValue value, CancellationToken cancellationToken)
        {
            _values[reference.Name] = value.Reveal();
            return Task.CompletedTask;
        }
        public Task<bool> DeleteAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(_values.TryRemove(reference.Name, out _));
        public Task<bool> ExistsAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(_values.ContainsKey(reference.Name));
    }
    private sealed class EphemeralStagingStore : IGoogleDriveStagingStore
    {
        public async Task<GoogleDriveStagedFile> StageAsync(Stream content, long expectedLength, ContentChecksum checksum, CancellationToken token)
        {
            var path = Path.GetTempFileName();
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await content.CopyToAsync(file, token);
            }
            return new GoogleDriveStagedFile(path, token, () => { try { File.Delete(path); } catch { } return ValueTask.CompletedTask; });
        }
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        private int _active;
        private int _sequence;
        private int _refreshRequests;
        public int MaxConcurrentRefreshes { get; private set; }
        public int AuthorizationExchanges { get; private set; }
        public ConcurrentQueue<string> RefreshTokens { get; } = new();
        public string? TokenError { get; init; }
        public bool OmitAuthorizationRefreshToken { get; init; }
        public bool PauseFirstRefresh { get; init; }
        public bool PauseAuthorization { get; init; }
        public bool PauseRevocation { get; init; }
        public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AuthorizationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeAuthorization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RevocationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeRevocation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal))
            {
                var fields = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (fields["grant_type"] == "authorization_code")
                {
                    AuthorizationExchanges++;
                    AuthorizationEntered.TrySetResult();
                    if (PauseAuthorization) await ResumeAuthorization.Task.WaitAsync(cancellationToken);
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "access", refresh_token = OmitAuthorizationRefreshToken ? null : "authorized", expires_in = 3600 }) };
                }
                var active = Interlocked.Increment(ref _active);
                MaxConcurrentRefreshes = Math.Max(MaxConcurrentRefreshes, active);
                try
                {
                    RefreshTokens.Enqueue(fields["refresh_token"]!);
                    if (Interlocked.Increment(ref _refreshRequests) == 1)
                    {
                        RefreshEntered.TrySetResult();
                        if (PauseFirstRefresh) await ResumeRefresh.Task.WaitAsync(cancellationToken);
                    }
                    await Task.Delay(100, cancellationToken);
                    if (TokenError is not null)
                        return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = TokenError, error_description = "invalid_grant is not the error code unless explicitly supplied." }) };
                    var sequence = Interlocked.Increment(ref _sequence);
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "access", refresh_token = $"rotated-{sequence}", expires_in = 3600 }) };
                }
                finally { Interlocked.Decrement(ref _active); }
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal))
            {
                RevocationEntered.TrySetResult();
                if (PauseRevocation) await ResumeRevocation.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.OK);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { user = new { emailAddress = "test@example.test", displayName = "Test" } }) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { files = Array.Empty<object>() }) };
        }
    }
}
