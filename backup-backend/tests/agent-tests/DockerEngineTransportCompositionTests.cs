using System.Reflection;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Host;
using BackupPlatform.Agent.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.AgentTests;

public sealed class DockerEngineTransportCompositionTests
{
    [Fact]
    public void TheTransportKeepsItsSocketBoundClientEvenWhenAnHttpClientIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient());
        services.AddAgentDiscovery();
        using var provider = services.BuildServiceProvider();

        var transport = Assert.IsType<DockerEngineHttpTransport>(provider.GetRequiredService<IDockerEngineTransport>());

        // Only the parameterless constructor builds the Unix-socket / named-pipe client and owns it; an injected client reaches no Docker daemon.
        var owns = typeof(DockerEngineHttpTransport).GetField("ownsClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)owns.GetValue(transport)!);
    }
}
