using System.Net;
using System.Net.Http.Headers;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class HttpAgentBackendClientTests
{
    private readonly FakeHttpHandler _handler = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly HttpAgentBackendClient _client;

    public HttpAgentBackendClientTests() =>
        _client = new HttpAgentBackendClient(
            new HttpClient(_handler), new AgentBackendEndpoint(new Uri("https://backup.example.test/api-root")), _time);

    private static readonly AgentHeartbeatRequest Heartbeat = new()
    {
        AgentVersion = "1.2.3",
        Platform = AgentPlatformNames.Linux,
        Runtime = new AgentRuntimeInfo { OsDescription = "Linux", RuntimeVersion = "10.0.0" },
    };

    private Task<AgentBackendResult<AgentHeartbeatResponse>> SendHeartbeat(CancellationToken ct = default) =>
        _client.SendHeartbeatAsync(TestIdentity.Create(), Heartbeat, ct);

    private static string ExpectedAuthorization => $"BackupAgent {TestIdentity.CredentialId:N}.{TestIdentity.Secret}";

    [Fact]
    public async Task Heartbeat_posts_camel_case_json_with_credential_and_protocol_headers()
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """
            {"serverTime":"2026-01-01T00:00:05+00:00","heartbeatIntervalSeconds":20,"offlineTimeoutSeconds":60,
             "protocol":{"current":"1.0","minimumSupported":"1.0","maximumSupported":"1.0"},"capabilitySyncRequested":false}
            """));

        var result = await SendHeartbeat();

        Assert.True(result.IsSuccess);
        Assert.Equal(20, result.Value.HeartbeatIntervalSeconds);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://backup.example.test/api-root/api/agent/heartbeat", request.Uri.ToString());
        Assert.Equal(ExpectedAuthorization, request.Headers["Authorization"]);
        Assert.Equal(AgentProtocol.Current.Version.ToString(), request.Headers[AgentProtocolHeader.Name]);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Contains("\"agentVersion\":\"1.2.3\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"platform\":\"linux\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"osDescription\":\"Linux\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(TestIdentity.Secret, request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_gets_tasks_with_the_escaped_cursor_in_the_query_and_the_secret_only_in_the_header()
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"cursor":"next","tasks":[],"nextPollAfterSeconds":45}"""));

        var first = await _client.PollAsync(TestIdentity.Create(), null, default);
        var second = await _client.PollAsync(TestIdentity.Create(), "a b/c+d=", default);

        Assert.True(first.IsSuccess);
        Assert.Equal("next", first.Value.Cursor);
        Assert.Equal(45, second.Value!.NextPollAfterSeconds);
        var requests = _handler.Requests;
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("https://backup.example.test/api-root/api/agent/tasks", requests[0].Uri.ToString());
        Assert.Equal("?cursor=a%20b%2Fc%2Bd%3D", requests[1].Uri.Query);
        Assert.All(requests, r => Assert.Equal(ExpectedAuthorization, r.Headers["Authorization"]));
        Assert.All(requests, r => Assert.DoesNotContain(TestIdentity.Secret, r.Uri.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_call_uses_the_credential_of_the_identity_it_is_given()
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c"}"""));
        var rotatedId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await _client.PollAsync(TestIdentity.Create(), null, default);
        await _client.PollAsync(TestIdentity.Create(TestIdentity.RotatedSecret, rotatedId), null, default);

        var requests = _handler.Requests;
        Assert.Equal(ExpectedAuthorization, requests[0].Headers["Authorization"]);
        Assert.Equal($"BackupAgent {rotatedId:N}.{TestIdentity.RotatedSecret}", requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Unauthorized_maps_to_the_unauthorized_failure()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Headers = { WwwAuthenticate = { new AuthenticationHeaderValue("BackupAgent") } },
        });

        var result = await SendHeartbeat();

        Assert.False(result.IsSuccess);
        Assert.Equal(AgentBackendFailureKind.Unauthorized, result.Failure.Kind);
        Assert.Equal(401, result.Failure.StatusCode);
    }

    [Theory]
    [InlineData("protocol_incompatible", AgentErrorActions.UpgradeAgent)]
    [InlineData("protocol_missing", AgentErrorActions.UpgradeAgent)]
    [InlineData("protocol_incompatible", AgentErrorActions.WaitForServerUpgrade)]
    public async Task Protocol_errors_map_to_protocol_incompatible_with_the_error_body(string error, string action)
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.BadRequest,
            $$$"""{"error":"{{{error}}}","action":"{{{action}}}","supportedProtocol":{"current":"2.0","minimumSupported":"2.0","maximumSupported":"2.1"}}"""));

        var result = await SendHeartbeat();

        Assert.Equal(AgentBackendFailureKind.ProtocolIncompatible, result.Failure!.Kind);
        Assert.Equal(error, result.Failure.Error!.Error);
        Assert.Equal(action, result.Failure.Error.Action);
        Assert.Equal("2.0", result.Failure.Error.SupportedProtocol!.MinimumSupported);
    }

    [Theory]
    [InlineData("""{"error":"invalid_request"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Other_bad_requests_are_plain_rejections(string body)
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.BadRequest, body));

        var result = await SendHeartbeat();

        Assert.Equal(AgentBackendFailureKind.Rejected, result.Failure!.Kind);
    }

    [Fact]
    public async Task Rate_limit_reads_retry_after_from_the_delta_header()
    {
        _handler.Respond = (_, _) =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"error":"rate_limited","retryAfterSeconds":5}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return Task.FromResult(response);
        };

        var result = await SendHeartbeat();

        Assert.Equal(AgentBackendFailureKind.RateLimited, result.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(42), result.Failure.RetryAfter);
    }

    [Fact]
    public async Task Rate_limit_reads_retry_after_from_an_http_date_relative_to_the_clock()
    {
        _handler.Respond = (_, _) =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(_time.GetUtcNow().AddSeconds(90));
            return Task.FromResult(response);
        };

        var result = await SendHeartbeat();

        Assert.Equal(TimeSpan.FromSeconds(90), result.Failure!.RetryAfter);
    }

    [Fact]
    public async Task Rate_limit_falls_back_to_the_body_and_then_to_no_hint()
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"error":"rate_limited","retryAfterSeconds":17}"""));
        var withBody = await SendHeartbeat();
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var bare = await SendHeartbeat();

        Assert.Equal(TimeSpan.FromSeconds(17), withBody.Failure!.RetryAfter);
        Assert.Null(bare.Failure!.RetryAfter);
        Assert.Equal(AgentBackendFailureKind.RateLimited, bare.Failure.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, AgentBackendFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, AgentBackendFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentBackendFailureKind.Transient)]
    [InlineData(HttpStatusCode.RequestTimeout, AgentBackendFailureKind.Transient)]
    [InlineData(HttpStatusCode.NotFound, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, AgentBackendFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Redirect, AgentBackendFailureKind.Rejected)]
    public async Task Other_statuses_map_to_transient_or_rejected(HttpStatusCode status, AgentBackendFailureKind expected)
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status));

        var result = await SendHeartbeat();

        Assert.Equal(expected, result.Failure!.Kind);
        Assert.Equal((int)status, result.Failure.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    public async Task Unreadable_success_bodies_are_transient_failures_not_exceptions(string body)
    {
        _handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, body));

        var result = await SendHeartbeat();

        Assert.Equal(AgentBackendFailureKind.Transient, result.Failure!.Kind);
    }

    [Fact]
    public async Task Network_errors_are_transient_failures_and_never_throw()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("Connection refused (backup.example.test:443)", new IOException("refused"));

        var result = await SendHeartbeat();

        Assert.Equal(AgentBackendFailureKind.Transient, result.Failure!.Kind);
        Assert.Contains("Connection refused", result.Failure.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_timeout_is_a_transient_failure_but_caller_cancellation_still_throws()
    {
        _handler.Respond = (_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var timedOut = await SendHeartbeat();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _handler.Respond = (_, ct) => Task.FromCanceled<HttpResponseMessage>(ct);

        Assert.Equal(AgentBackendFailureKind.Transient, timedOut.Failure!.Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendHeartbeat(cts.Token));
    }

    [Fact]
    public async Task Secret_and_authorization_never_appear_in_any_failure_description()
    {
        var responses = new Queue<Func<HttpResponseMessage>>(
        [
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"protocol_incompatible","action":"upgrade_agent"}"""),
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            () => new HttpResponseMessage(HttpStatusCode.BadGateway),
            () => throw new HttpRequestException("socket closed"),
        ]);
        _handler.Respond = (_, _) => Task.FromResult(responses.Dequeue()());

        var texts = new List<string>();
        while (responses.Count > 0)
        {
            var result = await SendHeartbeat();
            texts.Add(result.Failure!.Description + result.Failure.Error);
        }

        var all = string.Join('\n', texts);
        Assert.DoesNotContain(TestIdentity.Secret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupAgent ", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Endpoint_requires_an_absolute_https_uri()
    {
        Assert.Throws<ArgumentException>(() => new AgentBackendEndpoint(new Uri("http://backup.example.test/")));
        Assert.Throws<ArgumentException>(() => new AgentBackendEndpoint(new Uri("ws://backup.example.test/")));
        Assert.Throws<ArgumentException>(() => new AgentBackendEndpoint(new Uri("/relative", UriKind.Relative)));
        Assert.Equal("https://backup.example.test/", new AgentBackendEndpoint(new Uri("https://backup.example.test")).BaseUri.ToString());
        Assert.Equal("https://backup.example.test/a/", new AgentBackendEndpoint(new Uri("https://backup.example.test/a?x=1")).BaseUri.ToString());
    }
}
