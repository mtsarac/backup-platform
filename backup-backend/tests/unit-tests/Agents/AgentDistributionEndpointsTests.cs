using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BackupPlatform.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.UnitTests.Agents;

public sealed class AgentDistributionEndpointsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bp-dist-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentDistribution:PackagesDirectory"] = _directory,
            ["AgentDistribution:PublicBaseUrl"] = "https://backup.example/platform/"
        });
        _app = builder.Build();
        _app.MapAgentDistributionEndpoints();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("install.ps1")]
    [InlineData("install.sh")]
    public async Task Anonymous_get_and_head_serve_installers(string script)
    {
        using var response = await _client.GetAsync($"/api/agent-dist/{script}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/agent-dist/{script}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("win-x64", "backup-agent-win-x64.zip", "application/zip")]
    [InlineData("linux-x64", "backup-agent-linux-x64.tar.gz", "application/gzip")]
    public async Task Package_get_and_head_serve_only_the_configured_file(string platform, string fileName, string contentType)
    {
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(_directory, fileName), bytes);
        using var response = await _client.GetAsync($"/api/agent-dist/packages/{platform}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(fileName, response.Content.Headers.ContentDisposition!.ToString());
        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/agent-dist/packages/{platform}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    [InlineData("WIN-X64")]
    [InlineData("macos")]
    public async Task Package_aliases_and_unknown_platforms_are_rejected(string platform)
    {
        using var response = await _client.GetAsync($"/api/agent-dist/packages/{platform}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_or_empty_packages_are_unavailable_and_status_matches_downloads()
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "backup-agent-win-x64.zip"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "backup-agent-linux-x64.tar.gz"), []);
        using var missing = await _client.GetAsync("/api/agent-dist/packages/linux-x64");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var status = JsonDocument.Parse(await _client.GetStringAsync("/api/agent-dist/status"));
        Assert.Equal("https://backup.example/platform", status.RootElement.GetProperty("serverUrl").GetString());
        Assert.True(status.RootElement.GetProperty("packages").GetProperty("win-x64").GetBoolean());
        Assert.False(status.RootElement.GetProperty("packages").GetProperty("linux-x64").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative/path")]
    public async Task Unconfigured_or_relative_package_directory_never_falls_back(string? directory)
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "backup-agent-win-x64.zip"), [1]);
        _app.Configuration["AgentDistribution:PackagesDirectory"] = directory;
        using var response = await _client.GetAsync("/api/agent-dist/packages/win-x64");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("http://backup.example")]
    [InlineData("https://user:password@backup.example")]
    [InlineData("https://backup.example/?token=secret")]
    public async Task Unsafe_public_urls_are_not_advertised(string url)
    {
        _app.Configuration["AgentDistribution:PublicBaseUrl"] = url;
        using var status = JsonDocument.Parse(await _client.GetStringAsync("/api/agent-dist/status"));
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("serverUrl").ValueKind);
    }

    [Theory]
    [InlineData("--non-interactive", "Server and enrollment token are required")]
    [InlineData("--non-interactive --server", "Missing option value")]
    [InlineData("--non-interactive --server https://backup.example --token invalid", "Invalid enrollment token")]
    [InlineData("--interactive --non-interactive", "Choose only one interaction mode")]
    [InlineData("--insecure", "Unknown installer option")]
    [InlineData("--server https://one.example --server https://two.example", "Duplicate installer option")]
    [InlineData("--non-interactive --language tr", "Sunucu ve kayıt anahtarı zorunludur")]
    [InlineData("--non-interactive --server http://backup.example --token 11111111111111111111111111111111.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Invalid HTTPS server address")]
    public async Task Linux_installer_rejects_invalid_automatic_arguments_without_terminal_reads(string arguments, string expectedError)
    {
        if (!OperatingSystem.IsLinux()) return;
        var script = await _client.GetStringAsync("/api/agent-dist/install.sh");
        var start = new ProcessStartInfo("bash", "-s -- " + arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(expectedError, await error);
        Assert.Empty(await output);
    }
}
