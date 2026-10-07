#if AGENT_PLATFORM_WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace BackupPlatform.Agent.Host;

internal static class WindowsEnrollmentCommand
{
    internal sealed record Input(string Server, string Token, string? Name, string StoreDirectory)
    {
        public override string ToString() => "Windows enrollment input [token redacted]";
    }

    internal static async Task<int> RunInstallerAsync(Input input, CancellationToken cancellationToken)
    {
        using var account = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(account).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new AgentEnrollmentException("Windows enrollment requires an elevated terminal to install NT SERVICE\\BackupAgent. Run this same enroll command as administrator, or use deploy\\windows\\Install-BackupAgentService.ps1 from elevated PowerShell. No identity was changed.");
        }
        if (File.Exists(Path.Combine(input.StoreDirectory, "agent-identity.bin")))
        {
            throw new AgentEnrollmentException("This store is already enrolled. The existing identity was preserved and the token was not used. Reinstall through Install-BackupAgentService.ps1 without EnrollmentToken.");
        }
        var installer = Path.Combine(AppContext.BaseDirectory, "deploy", "windows", "Install-BackupAgentService.ps1");
        if (!File.Exists(installer))
        {
            throw new AgentEnrollmentException("The published Windows installer is missing. Use a complete published agent folder containing deploy\\windows\\Install-BackupAgentService.ps1.");
        }
        // Only this fixed script is on the child command line. The transient token is passed over redirected stdin,
        // then remains a local variable in the installer process; it is not a child argument, environment or registry value.
        const string script = """
            $ErrorActionPreference = 'Stop'
            try {
                $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::ReadLine())) | ConvertFrom-Json
                & $request.installer -BinaryDirectory $request.binaryDirectory -BackendUri $request.server -EnrollmentToken $request.token -AgentName $request.name -StoreDirectory $request.storeDirectory
                $request = $null
                exit 0
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            start.ArgumentList.Add(argument);
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            installer,
            binaryDirectory = AppContext.BaseDirectory,
            server = input.Server,
            token = input.Token,
            name = input.Name,
            storeDirectory = input.StoreDirectory
        });
        try
        {
            using var process = Process.Start(start) ?? throw new AgentEnrollmentException("Windows PowerShell could not start the service installer.");
            await process.StandardInput.WriteLineAsync(Convert.ToBase64String(payload).AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            throw new AgentEnrollmentException("Windows PowerShell could not start the service installer. Check the published folder and administrator permissions.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
#endif
