#if AGENT_PLATFORM_WINDOWS
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using BackupPlatform.Agent.Core.Identity;
using Microsoft.Extensions.Hosting;

namespace BackupPlatform.Agent.Host;

internal sealed class WindowsEnrollmentWorker(string requestPath, string storeDirectory, IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly byte[] Entropy = "backup-platform/agent/enrollment-request/v1"u8.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var resultPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "result.json");
        var success = false;
        var message = "Service enrollment did not complete.";
        try
        {
            using var account = WindowsIdentity.GetCurrent();
            if (!string.Equals(account.Name, "NT SERVICE\\BackupAgent", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentEnrollmentException("Enrollment bootstrap must run as NT SERVICE\\BackupAgent through the Windows Service installer.");
            }
            VerifyStaging(requestPath, account.User!);
            var protectedData = await File.ReadAllBytesAsync(requestPath, stoppingToken).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.LocalMachine);
            File.Delete(requestPath);
            EnrollmentInput input;
            try
            {
                input = JsonSerializer.Deserialize<EnrollmentInput>(plain, JsonSerializerOptions.Web)
                    ?? throw new AgentEnrollmentException("The protected enrollment request is invalid.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
            using var lease = AgentEnrollmentLease.Acquire(storeDirectory);
            var store = AgentCredentialStoreFactory.Create(storeDirectory);
            using var http = AgentEnrollmentCommand.CreateHttpClient();
            var enrollment = new AgentEnrollment(http, store);
            if (string.IsNullOrEmpty(input.Token))
            {
                var identity = await store.LoadAsync(stoppingToken).ConfigureAwait(false)
                    ?? throw new AgentEnrollmentException("No saved identity exists. Supply an enrollment token or run the installer interactively.");
                await enrollment.VerifyAsync(identity, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await enrollment.EnrollAsync(AgentEnrollment.ValidateServer(input.Server), input.Token, input.Name, stoppingToken).ConfigureAwait(false);
            }
            success = true;
            message = "The service identity was loaded and verified successfully.";
        }
        catch (Exception exception) when (exception is AgentEnrollmentException or AgentCredentialStoreException or ArgumentException
            or IOException or UnauthorizedAccessException or CryptographicException or JsonException or OperationCanceledException)
        {
            message = exception is CryptographicException or JsonException or OperationCanceledException
                ? "The protected enrollment request could not be read, or enrollment was cancelled. Any existing identity was preserved."
                : AgentEnrollmentCommand.SafeError(exception);
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { success, message }), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { success = false; }
            Environment.ExitCode = success ? 0 : 1;
            lifetime.StopApplication();
        }
    }

    private static void VerifyStaging(string path, SecurityIdentifier service)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new AgentEnrollmentException("The enrollment staging path must not traverse a reparse point.");
            }
        }
        FileSystemSecurity[] security = [new FileInfo(path).GetAccessControl(), new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl()];
        foreach (var descriptor in security)
        {
            if (descriptor.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || (owner != service && owner != system && owner != admins))
            {
                throw new AgentEnrollmentException("The enrollment staging owner is not trusted.");
            }
            foreach (FileSystemAccessRule rule in descriptor.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && (rule.IdentityReference is not SecurityIdentifier sid
                    || (sid != service && sid != system && sid != admins)))
                {
                    throw new AgentEnrollmentException("The enrollment staging permissions admit an untrusted account.");
                }
            }
        }
    }

    private sealed class EnrollmentInput
    {
        public string Server { get; init; } = string.Empty;
        public string? Token { get; init; }
        public string? Name { get; init; }
    }
}
#endif
