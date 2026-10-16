extern alias AgentCore;

using System.Diagnostics;
using AgentCore::BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>Launches the actual published host. An optional deployment launcher can supply the test's existing shared SQL volume.</summary>
internal sealed class AgentHostSmokeProcess : IAsyncDisposable
{
    private readonly string _scratch;
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private readonly Action<string>? _diagnostic;
    private readonly string _storage;
    private AgentHostSmokeProcess(string scratch, string storage, Process process, Action<string>? diagnostic)
    {
        _scratch = scratch;
        _storage = storage;
        _process = process;
        _diagnostic = diagnostic;
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
    }

    public string StorageDirectory => _storage;

    /// <param name="localStorageRoot">An existing directory the caller owns and cleans up, to be the agent's local storage (the backend retention workers need to reach the same one); default: a directory inside the host's scratch space.</param>
    public static async Task<AgentHostSmokeProcess> StartAsync(AgentWorld world, EnrolledAgent agent, Action<string>? diagnostic = null, string? localStorageRoot = null)
    {
        var executable = Required("BACKUP_PLATFORM_TEST_AGENT_HOST_PATH");
        var scratch = Path.Combine(Path.GetTempPath(), "bp_agent_orchestration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var storage = localStorageRoot ?? Path.Combine(scratch, "storage");
        Directory.CreateDirectory(storage);
        var identityDirectory = Path.Combine(scratch, "identity");
        Directory.CreateDirectory(identityDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(identityDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var identity = new AgentLocalIdentity(agent.AgentId, world.ServerAddress!, agent.InstallationId,
            new AgentStoredCredential(agent.CredentialId, agent.Secret, 1));
        var identityPath = Path.Combine(identityDirectory, "agent-identity.json");
        await File.WriteAllBytesAsync(identityPath, AgentLocalIdentitySerializer.Serialize(identity));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(identityPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var certificatePath = Path.Combine(scratch, "backend.pem");
        await File.WriteAllTextAsync(certificatePath, world.Certificate!.ExportCertificatePem());
        var launcher = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_AGENT_HOST_LAUNCHER");
        var start = new ProcessStartInfo(launcher ?? executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (launcher is not null) start.ArgumentList.Add(executable);
        start.Environment["BACKUP_PLATFORM_AGENT_SMOKE_SCRATCH"] = scratch;
        start.Environment["SSL_CERT_FILE"] = certificatePath;
        start.Environment["Agent__Backend__BaseUri"] = world.ServerAddress!.AbsoluteUri;
        start.Environment["Agent__CredentialStoreDirectory"] = identityDirectory;
        start.Environment["Agent__Connectivity__HeartbeatInterval"] = "00:00:05";
        start.Environment["Agent__Connectivity__PollInterval"] = "00:00:05";
        start.Environment["Agent__Connectivity__PushFallbackDelay"] = "00:00:00";
        start.Environment["Agent__Connectivity__MinimumPollSpacing"] = "00:00:01";
        var serverStaging = Required("BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR");
        var agentStaging = Required("BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR");
        start.Environment["Agent__Backup__Staging__Mapping"] = serverStaging == agentStaging ? "SameHost" : "Mapped";
        start.Environment["Agent__Backup__Staging__ServerDirectory"] = serverStaging;
        start.Environment["Agent__Backup__Staging__AgentDirectory"] = agentStaging;
        start.Environment["Agent__Backup__LocalStorage__RootDirectory"] = storage;
        try
        {
            return new AgentHostSmokeProcess(scratch, storage, Process.Start(start) ?? throw new InvalidOperationException("Agent.Host could not start."), diagnostic);
        }
        catch
        {
            Directory.Delete(scratch, recursive: true);
            throw;
        }
    }

    public void AssertAlive() => Assert.False(_process.HasExited, _process.HasExited
        ? "Actual Agent.Host exited: " + _output.GetAwaiter().GetResult() + _error.GetAwaiter().GetResult() : null);

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited && !OperatingSystem.IsWindows())
        {
            using var signal = Process.Start(new ProcessStartInfo("kill")
            {
                ArgumentList = { "-TERM", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false
            });
            if (signal is not null) await signal.WaitForExitAsync();
        }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
        await Task.WhenAll(_output, _error);
        _diagnostic?.Invoke(await _output + await _error);
        _process.Dispose();
        Directory.Delete(_scratch, recursive: true);
    }

    internal static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} before running the real Agent.Host/MSSQL orchestration smoke.");
}
