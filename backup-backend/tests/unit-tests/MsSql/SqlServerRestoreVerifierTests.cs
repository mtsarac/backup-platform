using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Restore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreVerifierTests
{
    private static readonly DatabaseConnectionSettings Unreachable = new(new DatabaseEndpoint("127.0.0.1", 1));
    private static readonly PasswordDatabaseCredential Credential = new("nobody", new DatabaseSecret("irrelevant"));

    [Theory]
    [InlineData(DatabaseFailureKind.Timeout, "timeout")]
    [InlineData(DatabaseFailureKind.PermissionDenied, "permission-denied")]
    [InlineData(DatabaseFailureKind.AuthenticationFailed, "authentication-failed")]
    [InlineData(DatabaseFailureKind.Unreachable, "connection-failed")]
    [InlineData(DatabaseFailureKind.Unexpected, "unexpected-error")]
    [InlineData(DatabaseFailureKind.InvalidConfiguration, "unexpected-error")]
    public void EngineFailureKindsMapToStableReasonCodes(DatabaseFailureKind kind, string expected) =>
        Assert.Equal(expected, SqlServerVerificationReasons.ForKind(kind));

    [Fact]
    public async Task OnlyTheVerificationKindCanBeDroppedAndNothingIsContactedForTheOther()
    {
        var provider = new SqlServerRestoreProvider(NullLogger<SqlServerRestoreProvider>.Instance);

        // The unreachable endpoint proves the refusal comes before any connection is attempted.
        var result = await provider.DropDatabaseAsync(
            Unreachable, Credential, new DatabaseDropRequest("bp_old_0123456789abcdef0123456789abcdef", DatabaseDropKind.RollbackCopy), CancellationToken.None);

        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Failed, "unsupported-kind"), result);
    }

    [Fact]
    public async Task AnUnreachableServerIsAFailedDropAndAFailedFirstCheckNotAnException()
    {
        var provider = new SqlServerRestoreProvider(NullLogger<SqlServerRestoreProvider>.Instance);
        var name = RestoreDatabaseNames.NewVerificationName();

        var drop = await provider.DropDatabaseAsync(Unreachable, Credential, new DatabaseDropRequest(name, DatabaseDropKind.VerificationDatabase), CancellationToken.None);
        var report = await provider.VerifyDatabaseAsync(Unreachable, Credential, new RestoreVerificationRequest(name), CancellationToken.None);

        Assert.Equal(DatabaseDropOutcome.Failed, drop.Outcome);
        Assert.Equal("connection-failed", drop.ReasonCode);
        var check = Assert.Single(report.Checks);
        Assert.Equal((RestoreVerificationCheck.DatabaseOnline, false, "connection-failed"), (check.CheckId, check.Passed, check.ReasonCode));
        Assert.False(report.Passed);
    }
}
