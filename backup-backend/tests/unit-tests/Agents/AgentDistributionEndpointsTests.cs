using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BackupPlatform.Agents;
using BackupPlatform.Agents.Enrollment;
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
    private readonly FakeSettingsStore _settingsStore = new();
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
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new AgentSecurityOptions());
        builder.Services.AddSingleton<IPlatformAgentSettingsStore>(_settingsStore);
        builder.Services.AddSingleton<PlatformAgentSettingsService>();
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

    [Fact]
    public async Task Setup_wizard_is_served_with_the_server_address_stamped_after_the_file_and_status_reports_it()
    {
        using var absent = JsonDocument.Parse(await _client.GetStringAsync("/api/agent-dist/status"));
        Assert.False(absent.RootElement.GetProperty("packages").GetProperty("win-x64-setup").GetBoolean());
        using var missing = await _client.GetAsync("/api/agent-dist/packages/win-x64-setup");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(_directory, "backup-agent-win-x64-setup.exe"), bytes);
        _settingsStore.State = new PlatformAgentSettingsState("https://stored.example", 1440, DateTimeOffset.UtcNow);

        using var response = await _client.GetAsync("/api/agent-dist/packages/win-x64-setup");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("backup-agent-win-x64-setup.exe", response.Content.Headers.ContentDisposition!.ToString());
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, body[..bytes.Length]);
        Assert.Equal("\nBackupPlatformServer=https://stored.example\n", System.Text.Encoding.UTF8.GetString(body[bytes.Length..]));
        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/agent-dist/packages/win-x64-setup"));
        Assert.Equal(body.Length, head.Content.Headers.ContentLength);
        using var status = JsonDocument.Parse(await _client.GetStringAsync("/api/agent-dist/status"));
        Assert.True(status.RootElement.GetProperty("packages").GetProperty("win-x64-setup").GetBoolean());
    }

    [Fact]
    public async Task Setup_wizard_is_refused_while_no_server_address_can_be_embedded()
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "backup-agent-win-x64-setup.exe"), [1]);
        _app.Configuration["AgentDistribution:PublicBaseUrl"] = null;
        using var response = await _client.GetAsync("/api/agent-dist/packages/win-x64-setup");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
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

    [Fact]
    public async Task Stored_server_url_takes_precedence_over_configuration_fallback()
    {
        _settingsStore.State = new PlatformAgentSettingsState("https://stored.example", 1440, DateTimeOffset.UtcNow);
        using var status = JsonDocument.Parse(await _client.GetStringAsync("/api/agent-dist/status"));
        Assert.Equal("https://stored.example", status.RootElement.GetProperty("serverUrl").GetString());
    }

    private sealed class FakeSettingsStore : IPlatformAgentSettingsStore
    {
        public PlatformAgentSettingsState? State { get; set; }
        public Task<PlatformAgentSettingsState?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task SaveAsync(string? serverUrl, int enrollmentTokenLifetimeMinutes, BackupPlatform.Domain.Identity.UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            State = new PlatformAgentSettingsState(serverUrl, enrollmentTokenLifetimeMinutes, now);
            return Task.CompletedTask;
        }
    }
    // The refusal sentence is the same on both platforms (documented in the root README) and is followed by a hint naming the consent switch.
    // The Windows script cannot run here, so both are compared as text; the Linux checks all end in one helper so no other wording can reach the operator.
    private const string ExistingInstallEnglish = "An existing agent installation was found; nothing was replaced. Uninstall the existing agent first, then run this installer again. Alternatively run it in a terminal to be asked, or add ";
    private const string ExistingInstallTurkish = "Mevcut bir ajan kurulumu bulundu; hicbir sey degistirilmedi. Once mevcut ajani kaldirin, ardindan bu kurulumu yeniden calistirin. Ya da sorulmasi icin bir terminalde calistirin, veya sormadan kaldirip yeniden kurmak icin ";

    [Theory]
    [InlineData("install.sh", "refuse_existing() { fail ", "--replace-existing")]
    [InlineData("install.ps1", "function Refuse-Existing { Fail ", "-ReplaceExisting")]
    public async Task Installers_refuse_an_existing_install_without_consent_with_the_same_message_and_name_the_switch(string script, string call, string option)
    {
        var text = await _client.GetStringAsync($"/api/agent-dist/{script}");

        Assert.Contains($"{call}'{ExistingInstallEnglish}{option} to remove", text, StringComparison.Ordinal);
        Assert.Contains($"' '{ExistingInstallTurkish}{option} ekleyin.'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("service is already registered", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Existing agent binaries", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install.sh", "--replace-existing", "Invalid archive root", "\n    remove_existing\n", "[[ \"$confirm\" =~ $accepted ]] || refuse_existing")]
    [InlineData("install.ps1", "-ReplaceExisting", "Invalid release layout", "\n        Remove-ExistingAgent $installPath $storePath\n", "if ($answer -notmatch $accepted) { Refuse-Existing }")]
    public async Task Installers_ask_before_replacing_default_to_no_and_remove_only_after_the_package_is_validated(string script, string option, string packageCheck, string removeCall, string declined)
    {
        var text = await _client.GetStringAsync($"/api/agent-dist/{script}");

        Assert.Contains("Remove it and reinstall? (y/n)", text, StringComparison.Ordinal);
        Assert.Contains("Mevcut ajan kurulumu bulundu. Kaldirilip yeniden kurulsun mu? (e/h)", text, StringComparison.Ordinal);
        Assert.Contains("Backups already stored in backup storage locations are not deleted.", text, StringComparison.Ordinal);
        Assert.Contains(option, text, StringComparison.Ordinal);
        // Only an explicit y/e answer proceeds; everything else (including Enter) refuses.
        Assert.Contains(declined, text, StringComparison.Ordinal);
        Assert.Contains("'^[eE]$'", text, StringComparison.Ordinal);
        Assert.Contains("'^[yY]$'", text, StringComparison.Ordinal);
        var validated = text.IndexOf(packageCheck, StringComparison.Ordinal);
        var removed = text.IndexOf(removeCall, StringComparison.Ordinal);
        Assert.True(validated >= 0 && removed > validated, "the old installation must be removed only after the new package was downloaded and validated");
        Assert.True(text.IndexOf(declined, StringComparison.Ordinal) < removed, "the consent question must precede the removal");
        Assert.DoesNotMatch(@"[^\x00-\x7F]", text.TrimStart('\uFEFF'));
    }

    [Fact]
    public async Task Windows_replace_removes_the_update_staging_directory_next_to_the_store_after_checking_it_like_the_store()
    {
        var install = await _client.GetStringAsync("/api/agent-dist/install.ps1");
        var removal = install[install.IndexOf("function Remove-ExistingAgent", StringComparison.Ordinal)..];

        Assert.Contains("\"$store.updates\"", removal, StringComparison.Ordinal);
        var preflight = removal.IndexOf("Assert-PurgeUpdateStage", StringComparison.Ordinal);
        var deletion = removal.IndexOf("Remove-Item -LiteralPath $stage", StringComparison.Ordinal);
        Assert.True(preflight >= 0 && deletion > preflight, "the update staging directory must be validated before anything is deleted");
    }

    [Theory]
    [InlineData("--non-interactive", "Server and enrollment token are required")]
    [InlineData("--non-interactive --server", "Missing option value")]
    [InlineData("--non-interactive --server https://backup.example --token invalid", "Invalid enrollment token")]
    [InlineData("--interactive --non-interactive", "Choose only one interaction mode")]
    [InlineData("--insecure", "Unknown installer option")]
    [InlineData("--server https://one.example --server https://two.example", "Duplicate installer option")]
    [InlineData("--non-interactive --language tr", "Sunucu ve kayit anahtari zorunludur")]
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
        // bash may exit on the argument error before reading the whole script from stdin.
        try { await process.StandardInput.WriteAsync(script); }
        catch (IOException) { }
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(expectedError, await error);
        Assert.Empty(await output);
    }
}
