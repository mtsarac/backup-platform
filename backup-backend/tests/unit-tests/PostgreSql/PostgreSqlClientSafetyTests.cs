using System.Diagnostics;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Tools;

namespace BackupPlatform.UnitTests.PostgreSql;

public sealed class PostgreSqlClientSafetyTests
{
    [Theory]
    [InlineData("pg_dump (PostgreSQL) 18.6 (Debian 18.6-1)", 18, 6)]
    [InlineData("pg_restore (PostgreSQL) 17beta1", 17, 0)]
    [InlineData("pg_dump (PostgreSQL) 19devel", 19, 0)]
    [InlineData("pg_dump (PostgreSQL) 9.6.24", 9, 6)]
    public void Client_version_is_not_confused_with_package_version(string value, int major, int minor)
    {
        Assert.True(PostgreSqlClientTools.TryParseVersion(value, out var actualMajor, out var actualMinor));
        Assert.Equal(major, actualMajor);
        Assert.Equal(minor, actualMinor);
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("name' host=attacker", "'name\\' host=attacker'")]
    [InlineData("a\\b", "'a\\\\b'")]
    [InlineData("postgresql://evil/secret", "'postgresql://evil/secret'")]
    public void Database_names_cannot_expand_into_connection_parameters(string name, string expected) =>
        Assert.Equal(expected, PostgreSqlConnectionTarget.QuoteConninfo(name));

    [Fact]
    public void Name_limit_is_UTF8_bytes_not_characters()
    {
        Assert.True(PostgreSqlIdentifiers.IsValidDatabaseName(new string('a', 63)));
        Assert.False(PostgreSqlIdentifiers.IsValidDatabaseName(new string('a', 64)));
        Assert.True(PostgreSqlIdentifiers.IsValidDatabaseName(new string('ü', 31)));
        Assert.False(PostgreSqlIdentifiers.IsValidDatabaseName(new string('ü', 32)));
        Assert.False(PostgreSqlIdentifiers.IsValidDatabaseName("contains\0NUL"));
    }

    [Fact]
    public void Password_is_not_in_tool_arguments_or_rendered_target()
    {
        var target = PostgreSqlConnectionTarget.Create(new DatabaseConnectionSettings(new DatabaseEndpoint("localhost", 5432)),
            new PasswordDatabaseCredential("operator", new DatabaseSecret("private-password")));
        Assert.DoesNotContain("private-password", target.ToConninfo("source"));
        Assert.DoesNotContain("private-password", target.ToString());
    }

    [Fact]
    public async Task Cancellation_joins_the_real_child_before_returning()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = SleepSpec(TimeSpan.FromMinutes(2));
        var execution = PostgreSqlProcessRunner.RunAsync(spec, null, line =>
        {
            if (int.TryParse(line.Trim(), out var pid)) started.TrySetResult(pid);
            return true;
        }, cancellation.Token);
        var childPid = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        AssertChildExited(childPid);
    }

    [Fact]
    public async Task Timeout_stops_child_and_is_not_reported_as_caller_cancellation()
    {
        int? childPid = null;
        var result = await PostgreSqlProcessRunner.RunAsync(SleepSpec(TimeSpan.FromSeconds(2)), null, line =>
        {
            if (int.TryParse(line.Trim(), out var pid)) childPid = pid;
            return true;
        }, CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotNull(childPid);
        AssertChildExited(childPid.Value);
    }

    private static PostgreSqlProcessSpec SleepSpec(TimeSpan timeout) => OperatingSystem.IsWindows()
        ? new PostgreSqlProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$PID; Start-Sleep -Seconds 60"],
            new Dictionary<string, string>(), timeout, PostgreSqlStdoutMode.Lines)
        : new PostgreSqlProcessSpec("/bin/sh", ["-c", "echo $$; exec sleep 60"],
            new Dictionary<string, string>(), timeout, PostgreSqlStdoutMode.Lines);

    private static void AssertChildExited(int pid)
    {
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited, "The provider returned while its child was still running.");
        }
        catch (ArgumentException) { }
    }
}
