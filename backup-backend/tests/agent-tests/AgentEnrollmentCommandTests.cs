using System.Net;
using System.Text.Json;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Host;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.AgentTests;

public sealed class AgentEnrollmentCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agent-enroll-{Guid.NewGuid():N}");
    private readonly FakeHttpHandler _handler = new();
    private static readonly Uri Server = new("https://backup.example.test/platform/");
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly string Token = AgentSecretReference.Format(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "EnrollmentSecret".PadRight(43, 'z'));
    private IAgentCredentialStore Store => AgentCredentialStoreFactory.Create(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }

    private AgentEnrollment CreateEnrollment() => new(new HttpClient(_handler), Store);

    private void RespondSuccessfully() => _handler.Respond = (request, _) => Task.FromResult(request.Method == HttpMethod.Post
        ? FakeHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new AgentEnrollmentResponse
        {
            AgentId = TestIdentity.AgentId,
            Credential = new IssuedAgentCredential { CredentialId = TestIdentity.CredentialId, Secret = TestIdentity.Secret, Generation = 1 },
            Protocol = new AgentServerProtocol { Current = "1.0", MinimumSupported = "1.0", MaximumSupported = "1.0" }
        }, JsonSerializerOptions.Web))
        : FakeHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new AgentIdentityResponse
        {
            AgentId = TestIdentity.AgentId, TenantId = TenantId, CredentialId = TestIdentity.CredentialId, CredentialGeneration = 1
        }, JsonSerializerOptions.Web)));

    [Fact]
    public async Task Enrollment_persists_reloads_and_authenticates_the_saved_identity()
    {
        RespondSuccessfully();
        using var lease = AgentEnrollmentLease.Acquire(_directory);
        var enrolled = await CreateEnrollment().EnrollAsync(Server, Token, "Database host", CancellationToken.None);
        var restarted = await Store.LoadAsync(CancellationToken.None);
        Assert.NotNull(restarted);
        Assert.Equal(enrolled, restarted);
        Assert.Equal(Server, restarted.ServerUri);
        Assert.NotEqual(Guid.Empty, restarted.InstallationId);
        Assert.Equal(TestIdentity.Secret, restarted.Current.Secret);
        Assert.DoesNotContain(TestIdentity.Secret, enrolled.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, enrolled.ToString(), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_directory, "agent-identity.json")));
        }
    }

    [Fact]
    public async Task Already_enrolled_refuses_before_token_consumption_and_preserves_credential()
    {
        var previous = TestIdentity.Create();
        await Store.SaveAsync(previous, CancellationToken.None);
        using var lease = AgentEnrollmentLease.Acquire(_directory);
        var error = await Assert.ThrowsAsync<AgentEnrollmentException>(() => CreateEnrollment().EnrollAsync(Server, Token, null, CancellationToken.None));
        Assert.Contains("already enrolled", error.Message, StringComparison.Ordinal);
        Assert.Empty(_handler.Requests);
        Assert.Equal(previous, await Store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void Independent_enrollment_handles_cannot_consume_tokens_concurrently()
    {
        using (var lease = AgentEnrollmentLease.Acquire(_directory))
        {
            var error = Assert.Throws<AgentEnrollmentException>(() => AgentEnrollmentLease.Acquire(_directory));
            Assert.Contains("Another enrollment", error.Message, StringComparison.Ordinal);
        }
        using var nextLease = AgentEnrollmentLease.Acquire(_directory);
        Assert.True(nextLease.CanWrite);
    }

    [Fact]
    public async Task Failed_identity_verification_preserves_the_durable_identity_for_recovery()
    {
        RespondSuccessfully();
        var success = _handler.Respond;
        _handler.Respond = (request, cancellation) => request.Method == HttpMethod.Get
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)) : success(request, cancellation);
        using var lease = AgentEnrollmentLease.Acquire(_directory);
        var error = await Assert.ThrowsAsync<AgentEnrollmentException>(() => CreateEnrollment().EnrollAsync(Server, Token, null, CancellationToken.None));
        Assert.Contains("preserved", error.Message, StringComparison.Ordinal);
        Assert.Equal(TestIdentity.Secret, (await Store.LoadAsync(CancellationToken.None))!.Current.Secret);
        Assert.DoesNotContain(TestIdentity.Secret, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://backend.test")]
    [InlineData("https://user:password@backend.test")]
    [InlineData("https://backend.test?token=secret")]
    [InlineData("https://backend.test/#secret")]
    [InlineData("backend.test")]
    public void Unsafe_server_addresses_fail_without_echoing_input(string address)
    {
        var error = Assert.Throws<AgentEnrollmentException>(() => AgentEnrollment.ValidateServer(address));
        Assert.DoesNotContain(address, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("00000000000000000000000000000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Malformed_token_fails_before_backend_contact(string token)
    {
        await Assert.ThrowsAsync<AgentEnrollmentException>(() => CreateEnrollment().EnrollAsync(Server, token, null, CancellationToken.None));
        Assert.Empty(_handler.Requests);
        Assert.Null(await Store.LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("invalid_enrollment_token", "invalid or revoked")]
    [InlineData("enrollment_token_expired", "expired")]
    [InlineData("enrollment_token_consumed", "already used")]
    [InlineData("enrollment_disabled", "disabled")]
    public async Task Backend_token_errors_are_actionable_and_never_display_backend_secret_messages(string code, string expected)
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Unauthorized,
            JsonSerializer.Serialize(new AgentApiError { Error = code, Message = Token + TestIdentity.Secret }, JsonSerializerOptions.Web)));
        using var lease = AgentEnrollmentLease.Acquire(_directory);
        var error = await Assert.ThrowsAsync<AgentEnrollmentException>(() => CreateEnrollment().EnrollAsync(Server, Token, null, CancellationToken.None));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestIdentity.Secret, error.Message, StringComparison.Ordinal);
        Assert.Null(await Store.LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS connection failed")]
    [InlineData(HttpRequestError.NameResolutionError, "name could not be resolved")]
    [InlineData(HttpRequestError.ConnectionError, "backend could not be reached")]
    [InlineData(HttpRequestError.Unknown, "backend request failed")]
    public async Task Network_and_TLS_failures_have_distinct_safe_errors_and_do_not_save_identity(HttpRequestError failure, string expected)
    {
        _handler.Respond = (_, _) => throw new HttpRequestException(failure, "Transport diagnostic " + Token);
        using var lease = AgentEnrollmentLease.Acquire(_directory);
        var error = await Assert.ThrowsAsync<AgentEnrollmentException>(() => CreateEnrollment().EnrollAsync(Server, Token, null, CancellationToken.None));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
        Assert.Null(await Store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Secure_store_errors_fail_before_backend_contact_and_never_echo_stored_content()
    {
        using var lease = AgentEnrollmentLease.Acquire(_directory);
        var file = Path.Combine(_directory, OperatingSystem.IsWindows() ? "agent-identity.bin" : "agent-identity.json");
        await File.WriteAllTextAsync(file, Token + TestIdentity.Secret);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        var error = await Assert.ThrowsAsync<AgentCredentialStoreException>(() => CreateEnrollment().EnrollAsync(Server, Token, null, CancellationToken.None));
        Assert.Empty(_handler.Requests);
        Assert.DoesNotContain(Token, AgentEnrollmentCommand.SafeError(error), StringComparison.Ordinal);
        Assert.DoesNotContain(TestIdentity.Secret, AgentEnrollmentCommand.SafeError(error), StringComparison.Ordinal);
        Assert.Equal(Token + TestIdentity.Secret, await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "backend rejected")]
    [InlineData(HttpStatusCode.OK, "unreadable enrollment response")]
    public async Task Unavailable_or_malformed_backend_never_saves_unusable_identity(HttpStatusCode status, string expected)
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(status, "not-json"));
        using var lease = AgentEnrollmentLease.Acquire(_directory);
        var error = await Assert.ThrowsAsync<AgentEnrollmentException>(() => CreateEnrollment().EnrollAsync(Server, Token, null, CancellationToken.None));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Null(await Store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void Invalid_store_directory_is_rejected_before_enrollment()
    {
        Assert.Throws<AgentEnrollmentException>(() => AgentCredentialStoreFactory.ResolveDirectory("relative/store"));
    }

    [Fact]
    public void Enrollment_parser_supports_noninteractive_options_and_rejects_duplicates_without_secret_echo()
    {
        var options = AgentEnrollmentCommand.Parse(["enroll", "--server", Server.AbsoluteUri, "--token", Token, "--name", "Host", "--non-interactive", "--store-directory", _directory]);
        Assert.True(options.ContainsKey("--non-interactive"));
        Assert.Equal(_directory, options["--store-directory"]);
        var error = Assert.Throws<AgentEnrollmentException>(() => AgentEnrollmentCommand.Parse(["enroll", "--token", Token, "--token", Token]));
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Host\nName")]
    public async Task Invalid_agent_name_is_rejected_before_backend_contact(string name)
    {
        await Assert.ThrowsAsync<AgentEnrollmentException>(() => CreateEnrollment().EnrollAsync(Server, Token, name, CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Secure_save_failure_explains_consumed_token_and_does_not_claim_success()
    {
        RespondSuccessfully();
        var store = new DeniedSaveStore();
        var enrollment = new AgentEnrollment(new HttpClient(_handler), store);
        var error = await Assert.ThrowsAsync<AgentEnrollmentException>(() => enrollment.EnrollAsync(Server, Token, null, CancellationToken.None));
        Assert.Contains("token was consumed", error.Message, StringComparison.Ordinal);
        Assert.Contains("ownership, permissions and disk space", error.Message, StringComparison.Ordinal);
        Assert.Single(_handler.Requests);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    private sealed class DeniedSaveStore : IAgentCredentialStore
    {
        public Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<AgentLocalIdentity?>(null);
        public Task SaveAsync(AgentLocalIdentity identity, CancellationToken cancellationToken) =>
            throw new AgentCredentialStoreException("Access to the secure identity directory was denied.");
        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
