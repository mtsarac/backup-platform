using System.Net;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Agent.Transport;

namespace BackupPlatform.AgentTests;

public sealed class DockerSocketDiscoveryTests
{
    [Fact]
    public async Task SocketDiscoveryProducesNetworkReachablePublishedDatabaseWithoutInspectingSecrets()
    {
        var handler = new Handler(HttpStatusCode.OK, """[{"Names":["/orders"],"Image":"postgres:17","Ports":[{"IP":"0.0.0.0","PrivatePort":5432,"PublicPort":15432,"Type":"tcp"}]}]""");
        var result = await new DockerContainerDetector(new DockerEngineHttpTransport(new HttpClient(handler))).DetectAsync(new DiscoveryDetectionContext(IncludeDocker: true), default);
        var instance = Assert.Single(result.Instances);
        Assert.Equal(DiscoveryEngines.PostgreSql, instance.Engine);
        Assert.Equal(15432, instance.Port);
        Assert.Equal("orders", instance.ContainerName);
        Assert.Equal("/containers/json?all=false", handler.Path);
    }

    [Fact]
    public async Task SocketPermissionFailureIsUnavailableNotAnEmptySuccessfulScan()
    {
        var result = await new DockerContainerDetector(new DockerEngineHttpTransport(new HttpClient(new Handler(HttpStatusCode.Forbidden, "denied"))))
            .DetectAsync(new DiscoveryDetectionContext(IncludeDocker: true), default);
        Assert.Equal(DiscoverySourceStates.Unavailable, result.Status);
        Assert.Equal("docker_access_denied", result.ReasonCode);
    }

    [Theory]
    [InlineData("0.0.0.0", "tcp", 15432)]
    [InlineData("::", "tcp", 15432)]
    [InlineData("127.0.0.1", "tcp", 15432)]
    [InlineData("::1", "tcp", 15432)]
    [InlineData("192.168.1.5", "tcp", null)]
    [InlineData("0.0.0.0", "udp", null)]
    public async Task OnlyLoopbackReachableTcpBindingsBecomeBackupEndpoints(string address, string protocol, int? port)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new[] { new { Names = new[] { "/orders" }, Image = "postgres:17", Ports = new[] { new { IP = address, PrivatePort = 5432, PublicPort = 15432, Type = protocol } } } });
        using var client = new HttpClient(new Handler(HttpStatusCode.OK, body));
        var result = await new DockerContainerDetector(new DockerEngineHttpTransport(client)).DetectAsync(new DiscoveryDetectionContext(IncludeDocker: true), default);
        var instance = Assert.Single(result.Instances);
        Assert.Equal("postgres|docker|orders|5432", instance.Key);
        Assert.Equal(port, instance.Port);
        Assert.Equal(port is null ? "no_published_port" : null, instance.ReasonCode);
    }

    [Fact]
    public async Task DualStackBindingsCollapseAndMalformedJsonFailsHonestly()
    {
        using var client = new HttpClient(new Handler(HttpStatusCode.OK, """[{"Names":["/sql"],"Image":"mcr.microsoft.com/mssql/server:2022-latest","Ports":[{"IP":"0.0.0.0","PrivatePort":1433,"PublicPort":14333,"Type":"tcp"},{"IP":"::","PrivatePort":1433,"PublicPort":14333,"Type":"tcp"}]}]"""));
        var result = await new DockerContainerDetector(new DockerEngineHttpTransport(client)).DetectAsync(new DiscoveryDetectionContext(IncludeDocker: true), default);
        Assert.Equal(14333, Assert.Single(result.Instances).Port);
        using var malformed = new HttpClient(new Handler(HttpStatusCode.OK, "{not valid}"));
        Assert.Equal(DiscoverySourceStates.Failed, (await new DockerContainerDetector(new DockerEngineHttpTransport(malformed)).DetectAsync(new DiscoveryDetectionContext(IncludeDocker: true), default)).Status);
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
