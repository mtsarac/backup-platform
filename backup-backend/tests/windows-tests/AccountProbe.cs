using System.Diagnostics;
using System.Net;
using System.Security;
using System.Security.AccessControl;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>
/// Runs a probe in a real password-logon session, with that account's profile loaded. CreateProcessWithLogonW from a non-interactive
/// SSH/service session can fail during DLL initialisation because its desktop is not accessible to the second account.
/// Task Scheduler provides the account's own batch-logon session instead, without changing the invoking process's identity.
/// </summary>
internal static class AccountProbe
{
    public static ProcessResult Run(LocalAccount account, string executable, string[] arguments)
    {
        using var scratch = new TestDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var info = new DirectoryInfo(scratch.Root);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(account.Sid, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        var script = scratch.Combine("run.ps1");
        var output = scratch.Combine("output.txt");
        var exit = scratch.Combine("exit.txt");
        File.WriteAllText(script,
            $"& {Quote(executable)} {string.Join(' ', arguments.Select(Quote))} *> {Quote(output)}\n" +
            $"$code = $LASTEXITCODE\n[IO.File]::WriteAllText({Quote(exit)}, [string]$code)\nexit $code\n");
        var taskName = "bpw-probe-" + Guid.NewGuid().ToString("N");
        var xml = scratch.Combine("task.xml");
        File.WriteAllText(xml, $"""
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal id="Account"><UserId>{SecurityElement.Escape(account.Name)}</UserId><LogonType>Password</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT2M</ExecutionTimeLimit></Settings>
              <Actions Context="Account"><Exec><Command>powershell.exe</Command><Arguments>{SecurityElement.Escape("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"")}</Arguments></Exec></Actions>
            </Task>
            """);
        var password = new NetworkCredential(string.Empty, account.SecurePassword).Password;
        var created = Processes.Run("schtasks.exe", ["/Create", "/TN", taskName, "/XML", xml, "/RU", account.Name, "/RP", password, "/F"]);
        Assert.True(created.ExitCode == 0, created.Output.Replace(password, "***", StringComparison.Ordinal));
        try
        {
            var started = Processes.Run("schtasks.exe", ["/Run", "/TN", taskName]);
            Assert.True(started.ExitCode == 0, started.Output);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(exit))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(90))
                {
                    throw new TimeoutException("The second-account probe did not finish in its scheduled logon session.");
                }
                Thread.Sleep(100);
            }
            return new ProcessResult(int.Parse(File.ReadAllText(exit), System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(output).Trim());
        }
        finally
        {
            Processes.Run("schtasks.exe", ["/End", "/TN", taskName]);
            Processes.Run("schtasks.exe", ["/Delete", "/TN", taskName, "/F"]);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
