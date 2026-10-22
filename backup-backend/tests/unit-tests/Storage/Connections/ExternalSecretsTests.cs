using System.Net;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.SecretProviders.DataProtection;
using Microsoft.AspNetCore.DataProtection;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class ExternalSecretsTests
{
    [Fact]
    public async Task VaultReceivesOnlyProtectedCiphertextAndReferenceBoundValuesRoundTrip()
    {
        string? stored = null;
        var handler = new Handler(async request =>
        {
            Assert.Equal("vault-token", request.Headers.GetValues("X-Vault-Token").Single());
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.DoesNotContain("sensitive-password", body);
                using var json = System.Text.Json.JsonDocument.Parse(body);
                stored = json.RootElement.GetProperty("data").GetProperty("ciphertext").GetString();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { data = new { data = new { ciphertext = stored } } })) };
        });
        var repository = new VaultCiphertextRepository(new HttpClient(handler), new Uri("https://vault.example/"), "secret", "backup", () => "vault-token");
        var store = new DataProtectionSecretStore(new EphemeralDataProtectionProvider(), repository);
        var reference = new SecretReference("db-agent-1");
        await store.SetAsync(reference, new SecretValue("sensitive-password"), default);
        Assert.Equal("sensitive-password", (await store.ResolveAsync(reference, default)).Reveal());
    }

    [Fact]
    public async Task AwsRequestsAreSessionSignedAndRemoteErrorsNeverExposeBodies()
    {
        var handler = new Handler(request =>
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=access/", request.Headers.Authorization!.ToString());
            Assert.Equal("session", request.Headers.GetValues("X-Amz-Security-Token").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private server diagnostic") });
        });
        var repository = new AwsSecretsCiphertextRepository(new HttpClient(handler), "eu-west-1", "backup/", () => new AwsSecretCredentials("access", "secret", "session"));
        var error = await Assert.ThrowsAsync<SecretUnavailableException>(() => repository.GetAsync("db-agent-1", default));
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task AwsScheduledDeletionIsAbsentAndWritingTheSameReferenceRestoresIt()
    {
        var actions = new List<string>();
        var handler = new Handler(request =>
        {
            var action = request.Headers.GetValues("X-Amz-Target").Single().Split('.').Last();
            actions.Add(action);
            var response = action is "GetSecretValue" or "CreateSecret"
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"__type":"InvalidRequestException"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(action == "DescribeSecret" ? """{"DeletedDate":1760000000}""" : "{}") };
            return Task.FromResult(response);
        });
        var repository = new AwsSecretsCiphertextRepository(new HttpClient(handler), "eu-west-1", "backup/", () => new AwsSecretCredentials("access", "secret"));
        Assert.Null(await repository.GetAsync("reference", default));
        await repository.SetAsync("reference", "ciphertext", default);
        Assert.Equal(new[] { "GetSecretValue", "DescribeSecret", "CreateSecret", "DescribeSecret", "RestoreSecret", "PutSecretValue" }, actions);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
}
