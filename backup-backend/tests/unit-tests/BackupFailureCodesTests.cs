using System.Reflection;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.UnitTests;

public sealed class BackupFailureCodesTests
{
    private static readonly string[] Retryable =
    [
        BackupFailureCodes.DatabaseUnavailable,
        BackupFailureCodes.OperationTimedOut,
        BackupFailureCodes.StorageUnavailable,
        BackupFailureCodes.ReplicaVerificationFailed
    ];

    private static readonly string[] NonRetryable =
    [
        BackupFailureCodes.PreCheckFailed,
        BackupFailureCodes.SqlBackupFailed,
        BackupFailureCodes.VerificationFailed,
        BackupFailureCodes.HashFailed,
        BackupFailureCodes.StorageWriteFailed,
        BackupFailureCodes.InternalError,
        BackupFailureCodes.DatabaseNotFound,
        BackupFailureCodes.InvalidConfiguration,
        BackupFailureCodes.UnsupportedCapability,
        BackupFailureCodes.AuthenticationFailed,
        BackupFailureCodes.PermissionDenied,
        BackupFailureCodes.QuotaExceeded,
        BackupFailureCodes.StagingInvalid
    ];

    public static TheoryData<string> RetryableAgentCodes => new(Retryable);
    public static TheoryData<string> NonRetryableAgentCodes => new(NonRetryable);

    // The wire contract an agent may use with Status "Failed". Spelled out so a renamed or added code is a deliberate, reviewed change.
    private static readonly string[] DocumentedAgentReportable =
    [
        "PreCheckFailed", "SqlBackupFailed", "VerificationFailed", "HashFailed", "StorageWriteFailed", "ReplicaVerificationFailed", "InternalError",
        "DatabaseUnavailable", "OperationTimedOut", "StorageUnavailable", "DatabaseNotFound", "InvalidConfiguration", "UnsupportedCapability",
        "AuthenticationFailed", "PermissionDenied", "StagingInvalid", "QuotaExceeded"
    ];

    private static IEnumerable<string> AllDeclaredCodes() => typeof(BackupFailureCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!);

    [Theory]
    [MemberData(nameof(RetryableAgentCodes))]
    public void TransientCausesAreRetryable(string code) => Assert.True(BackupFailureCodes.IsRetryable(code));

    [Theory]
    [MemberData(nameof(NonRetryableAgentCodes))]
    public void PermanentOrUnclassifiedCausesAreNotRetryable(string code) => Assert.False(BackupFailureCodes.IsRetryable(code));

    [Fact]
    public void EveryDeclaredCodeIsClassifiedInTheRetryableOrNonRetryableTable()
    {
        var classified = Retryable.Concat(NonRetryable).Concat([BackupFailureCodes.Cancelled, BackupFailureCodes.AgentLost, BackupFailureCodes.ScheduleGraceExpired]);
        Assert.Equal(AllDeclaredCodes().Order(StringComparer.Ordinal), classified.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AgentLostIsRetryableButCancellationNeverIs()
    {
        Assert.True(BackupFailureCodes.IsRetryable(BackupFailureCodes.AgentLost));
        Assert.False(BackupFailureCodes.IsRetryable(BackupFailureCodes.Cancelled));
    }

    [Fact]
    public void ExpiredScheduledWorkIsNeitherAgentReportableNorRetryable()
    {
        // The platform ends the run itself when nobody claimed it within its grace period; an agent can never claim it, and retrying stale work is the point of the grace rule.
        Assert.False(BackupFailureCodes.IsAgentReportable(BackupFailureCodes.ScheduleGraceExpired));
        Assert.False(BackupFailureCodes.IsRetryable(BackupFailureCodes.ScheduleGraceExpired));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("SomethingNew")]
    [InlineData("SqlException: timeout expired; password=secret")]
    [InlineData("databaseunavailable")]
    [InlineData("DATABASEUNAVAILABLE")]
    [InlineData("agentlost")]
    [InlineData(" DatabaseUnavailable")]
    public void NullUnknownOrDifferentlyCasedCodesAreNotRetryable(string? code) => Assert.False(BackupFailureCodes.IsRetryable(code));

    [Fact]
    public void AgentReportableIsExactlyTheDocumentedSet()
    {
        Assert.Equal(DocumentedAgentReportable.Order(StringComparer.Ordinal), BackupFailureCodes.AgentReportable.Order(StringComparer.Ordinal));
        Assert.Equal(BackupFailureCodes.AgentReportable.Count, BackupFailureCodes.AgentReportable.Distinct().Count());
    }

    [Fact]
    public void EveryAgentReportableCodeIsClassifiedAndEveryRetryableOneIsAgentReportableOrAgentLost()
    {
        foreach (var code in DocumentedAgentReportable)
            Assert.True(BackupFailureCodes.IsAgentReportable(code), code);
        var classified = Retryable.Concat(NonRetryable).ToHashSet(StringComparer.Ordinal);
        Assert.True(classified.SetEquals(BackupFailureCodes.AgentReportable));
        foreach (var code in AllDeclaredCodes().Where(BackupFailureCodes.IsRetryable))
            Assert.True(BackupFailureCodes.IsAgentReportable(code) || code == BackupFailureCodes.AgentLost, code);
    }

    [Theory]
    [InlineData(BackupFailureCodes.Cancelled)]
    [InlineData(BackupFailureCodes.AgentLost)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomethingNew")]
    [InlineData("sqlbackupfailed")]
    public void CancelledAgentLostAndUnknownCodesAreNotAgentReportableFailures(string? code) =>
        Assert.False(BackupFailureCodes.IsAgentReportable(code));

    [Fact]
    public void AnAgentCannotReportAgentLostOrCancelledAsAFailedAttempt()
    {
        // Wiring check through the domain: the attempt state machine honours the same table.
        foreach (var code in new[] { BackupFailureCodes.AgentLost, BackupFailureCodes.Cancelled, "SqlException: boom" })
        {
            var run = NewRunningRun(out var token);
            Assert.Throws<ArgumentException>(() => run.Complete(token, Now.AddSeconds(1), new BackupCompletion(BackupStatus.Failed, code, null)));
            Assert.Equal(BackupStatus.Running, run.Status);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private static BackupRun NewRunningRun(out Guid token)
    {
        var run = new BackupRun(Guid.NewGuid(), BackupPlatform.Domain.Identity.TenantId.New(), Guid.NewGuid(), BackupPlatform.Domain.Identity.AgentId.New(),
            new BackupConfiguration("mssql", "Database", "localhost", null, true, null, true, false, "default", "", true, "Preferred", "Preferred") { Destinations = [new BackupDestination("local", null, true)] },
            "key", Now);
        run.QueueFirstAttempt(Guid.NewGuid(), Now);
        token = Guid.NewGuid();
        run.Claim(token, Now, TimeSpan.FromMinutes(2));
        run.Start(token, Now, "1.0", "1.0", "Linux");
        return run;
    }
}
