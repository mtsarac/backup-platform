using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Host;
using BackupPlatform.Agent.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BackupPlatform.AgentTests;

public sealed class OptionsValidationTests
{
    private static readonly string AbsoluteDirectory = Path.Combine(Path.GetTempPath(), "agent-options-test");

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ValidateOptionsResult Validate(Action<AgentHostOptions> change)
    {
        var options = new AgentHostOptions { Backend = { BaseUri = "https://backup.example.test/" } };
        change(options);
        return new AgentHostOptionsValidator().Validate(null, options);
    }

    [Fact]
    public void Defaults_match_the_documented_values_and_are_valid()
    {
        var options = new AgentConnectivityOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), options.PushFallbackDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), options.ReconnectBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ReconnectMaxDelay);
        Assert.Empty(options.Validate());
        Assert.True(Validate(_ => { }).Succeeded);
    }

    [Theory]
    [InlineData(nameof(AgentConnectivityOptions.HeartbeatInterval), 4)]
    [InlineData(nameof(AgentConnectivityOptions.HeartbeatInterval), 301)]
    [InlineData(nameof(AgentConnectivityOptions.PollInterval), 4)]
    [InlineData(nameof(AgentConnectivityOptions.PollInterval), 601)]
    [InlineData(nameof(AgentConnectivityOptions.PushFallbackDelay), -1)]
    [InlineData(nameof(AgentConnectivityOptions.PushFallbackDelay), 601)]
    [InlineData(nameof(AgentConnectivityOptions.ReconnectBaseDelay), 0)]
    [InlineData(nameof(AgentConnectivityOptions.ReconnectBaseDelay), -5)]
    [InlineData(nameof(AgentConnectivityOptions.ReconnectMaxDelay), 901)]
    [InlineData(nameof(AgentConnectivityOptions.MinimumPollSpacing), 0)]
    public void Each_out_of_range_value_is_reported_by_name(string property, int seconds)
    {
        var options = new AgentConnectivityOptions();
        typeof(AgentConnectivityOptions).GetProperty(property)!.SetValue(options, TimeSpan.FromSeconds(seconds));

        var problems = options.Validate();

        var problem = Assert.Single(problems);
        Assert.StartsWith(property, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(AgentConnectivityOptions.HeartbeatInterval), 5)]
    [InlineData(nameof(AgentConnectivityOptions.HeartbeatInterval), 300)]
    [InlineData(nameof(AgentConnectivityOptions.PollInterval), 5)]
    [InlineData(nameof(AgentConnectivityOptions.PollInterval), 600)]
    [InlineData(nameof(AgentConnectivityOptions.PushFallbackDelay), 0)]
    [InlineData(nameof(AgentConnectivityOptions.PushFallbackDelay), 600)]
    [InlineData(nameof(AgentConnectivityOptions.ReconnectMaxDelay), 900)]
    public void Range_boundaries_are_inclusive(string property, int seconds)
    {
        var options = new AgentConnectivityOptions();
        typeof(AgentConnectivityOptions).GetProperty(property)!.SetValue(options, TimeSpan.FromSeconds(seconds));

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Reconnect_max_delay_must_not_be_smaller_than_the_base_delay()
    {
        var options = new AgentConnectivityOptions { ReconnectBaseDelay = TimeSpan.FromSeconds(30), ReconnectMaxDelay = TimeSpan.FromSeconds(10) };

        var problem = Assert.Single(options.Validate());

        Assert.StartsWith(nameof(AgentConnectivityOptions.ReconnectMaxDelay), problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Agent:Backend:BaseUri is required")]
    [InlineData("", "Agent:Backend:BaseUri is required")]
    [InlineData("   ", "Agent:Backend:BaseUri is required")]
    [InlineData("http://backup.example.test/", "Agent:Backend:BaseUri must be an absolute https URI")]
    [InlineData("ws://backup.example.test/", "Agent:Backend:BaseUri must be an absolute https URI")]
    [InlineData("backup.example.test", "Agent:Backend:BaseUri must be an absolute https URI")]
    [InlineData("/relative/path", "Agent:Backend:BaseUri must be an absolute https URI")]
    [InlineData("ftp://backup.example.test/", "Agent:Backend:BaseUri must be an absolute https URI")]
    [InlineData("https://user:pass@backup.example.test/", "user information")]
    public void Invalid_backend_address_is_reported(string? uri, string expectedProblem)
    {
        var result = Validate(o => o.Backend.BaseUri = uri);

        Assert.True(result.Failed);
        Assert.Contains(expectedProblem, Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("")]
    public void Credential_store_directory_must_be_absolute_when_set(string directory)
    {
        var result = Validate(o => o.CredentialStoreDirectory = directory);

        Assert.Contains("Agent:CredentialStoreDirectory", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_problem_is_listed_at_once()
    {
        var result = Validate(o =>
        {
            o.Backend.BaseUri = "http://insecure.example.test/";
            o.CredentialStoreDirectory = "relative";
            o.Connectivity.HeartbeatInterval = TimeSpan.FromSeconds(1);
            o.Connectivity.PollInterval = TimeSpan.FromHours(1);
            o.Connectivity.PushFallbackDelay = TimeSpan.FromHours(1);
            o.Connectivity.ReconnectBaseDelay = TimeSpan.Zero;
            o.Connectivity.ReconnectMaxDelay = TimeSpan.FromHours(1);
        });

        Assert.Equal(7, result.Failures!.Count());
        Assert.Contains(result.Failures!, f => f.StartsWith("Agent:Connectivity:HeartbeatInterval", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.StartsWith("Agent:Connectivity:PollInterval", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.StartsWith("Agent:Connectivity:PushFallbackDelay", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.StartsWith("Agent:Connectivity:ReconnectBaseDelay", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.StartsWith("Agent:Connectivity:ReconnectMaxDelay", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_binds_keys_including_environment_style_time_spans()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(Configuration(
            ("Agent:Backend:BaseUri", "https://backup.example.test/"),
            ("Agent:CredentialStoreDirectory", AbsoluteDirectory),
            ("Agent:Connectivity:HeartbeatInterval", "00:00:15"),
            ("Agent:Connectivity:PollInterval", "00:01:00"),
            ("Agent:Connectivity:PushFallbackDelay", "00:00:05"),
            ("Agent:Connectivity:ReconnectBaseDelay", "00:00:02"),
            ("Agent:Connectivity:ReconnectMaxDelay", "00:02:00")));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value;

        Assert.Equal("https://backup.example.test/", options.Backend.BaseUri);
        Assert.Equal(AbsoluteDirectory, options.CredentialStoreDirectory);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Connectivity.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Connectivity.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Connectivity.PushFallbackDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Connectivity.ReconnectBaseDelay);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Connectivity.ReconnectMaxDelay);
    }

    [Fact]
    public async Task Valid_configuration_wires_the_connectivity_service_to_the_transport()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(Configuration(
            ("Agent:Backend:BaseUri", "https://backup.example.test/base"),
            ("Agent:CredentialStoreDirectory", AbsoluteDirectory)));
        await using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<AgentConnectivityService>();

        Assert.Same(service, provider.GetRequiredService<IAgentConnectivityStatus>());
        Assert.IsType<SignalRAgentPushChannel>(provider.GetRequiredService<IAgentPushChannel>());
        var client = Assert.IsType<HttpAgentBackendClient>(provider.GetRequiredService<IAgentBackendClient>());
        Assert.Equal("https://backup.example.test/base/", client.BackendBaseUri.ToString());
        Assert.IsAssignableFrom<IAgentCredentialStore>(provider.GetRequiredService<IAgentCredentialStore>());
        Assert.Equal(AgentChannelState.Stopped, service.Current.State);
        Assert.IsType<HttpAgentCapabilityClient>(provider.GetRequiredService<IAgentCapabilityClient>());
        var source = Assert.IsType<CatalogCapabilityRevisionSource>(provider.GetRequiredService<ICapabilityRevisionSource>());
        Assert.Equal(provider.GetRequiredService<AgentCapabilityCatalog>().Revision, source.GetRevision());
    }

    [Fact]
    public async Task Invalid_configuration_fails_host_startup_naming_each_problem()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Backend:BaseUri"] = "http://insecure.example.test/",
            ["Agent:Connectivity:HeartbeatInterval"] = "00:00:01",
            ["Agent:Connectivity:ReconnectMaxDelay"] = "01:00:00",
        });
        builder.Services.AddLogging();
        builder.Services.AddAgentHost(builder.Configuration);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Agent:Backend:BaseUri must be an absolute https URI", error.Message, StringComparison.Ordinal);
        Assert.Contains("Agent:Connectivity:HeartbeatInterval", error.Message, StringComparison.Ordinal);
        Assert.Contains("Agent:Connectivity:ReconnectMaxDelay", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_constructor_rejects_invalid_options()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var options = new AgentConnectivityOptions { PollInterval = TimeSpan.FromSeconds(1) };

        var error = Assert.Throws<ArgumentException>(() => new AgentConnectivityService(
            new Support.FakeCredentialStore(null), new Support.FakeBackendClient(time), new Support.FakePushChannel(time),
            new AgentSoftwareInfo("1", "linux", null, null), new NullCapabilityRevisionSource(), options,
            new AgentBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentConnectivityService>.Instance));

        Assert.Contains("PollInterval", error.Message, StringComparison.Ordinal);
    }
}
