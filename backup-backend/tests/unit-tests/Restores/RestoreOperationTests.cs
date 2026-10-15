using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using static BackupPlatform.UnitTests.RestoreFixtures;

namespace BackupPlatform.UnitTests;

/// <summary>The restore state machine through its public API: every transition, its guard, and the pin that follows the lifecycle.</summary>
public sealed class RestoreOperationTests
{
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(10);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static void AssertPinReleasedAt(RestoreOperation operation, DateTimeOffset at)
    {
        Assert.False(operation.Pin.IsActive);
        Assert.Equal(at, operation.Pin.ReleasedAt);
        Assert.True(operation.IsTerminal);
        Assert.False(operation.IsActive);
        Assert.Equal(at, operation.CompletedAt);
    }

    // ---- construction ----

    [Fact]
    public void ANewOperationIsPendingWithAnActivePinAndNoAttempt()
    {
        var tenant = TenantId.New();
        var agent = AgentId.New();
        var id = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var replicaId = Guid.NewGuid();
        var operation = new RestoreOperation(id, tenant, artifactId, replicaId, RestoreSourceAccess.AgentLocal, agent, Target(), "Sales", RestoreMode.NewDatabase, false, null,
            "key-1", "fp-1", RequestedUserId, Now);

        Assert.Equal(id, operation.Id);
        Assert.Equal(tenant, operation.TenantId);
        Assert.Equal(artifactId, operation.ArtifactId);
        Assert.Equal(replicaId, operation.ReplicaId);
        Assert.Equal(RestoreSourceAccess.AgentLocal, operation.SourceAccess);
        Assert.Equal(agent, operation.AgentId);
        Assert.Equal(Target(), operation.Target);
        Assert.Equal("Sales", operation.DatabaseName);
        Assert.Equal(RestoreOptions.Default, operation.Options);
        Assert.Equal("key-1", operation.IdempotencyKey);
        Assert.Equal("fp-1", operation.RequestFingerprint);
        Assert.Equal(RequestedUserId, operation.RequestedByUserId);
        Assert.Equal(RestoreStatus.Pending, operation.Status);
        Assert.Equal(Now, operation.CreatedAt);
        Assert.Null(operation.StartedAt);
        Assert.Null(operation.CompletedAt);
        Assert.Null(operation.CancellationRequestedAt);
        Assert.Null(operation.FailureCode);
        Assert.Null(operation.TargetState);
        Assert.Null(operation.ResultJson);
        Assert.Equal(0, operation.CurrentAttemptNumber);
        Assert.Empty(operation.Attempts);
        Assert.True(operation.IsActive);
        Assert.False(operation.IsTerminal);
        Assert.True(operation.Pin.IsActive);
        Assert.Null(operation.Pin.ReleasedAt);
        Assert.Equal(id, operation.Pin.OperationId);
        Assert.Equal(replicaId, operation.Pin.ReplicaId);
        Assert.Equal(tenant, operation.Pin.TenantId);
        Assert.Equal(Now, operation.Pin.CreatedAt);
    }

    [Fact]
    public void ExplicitOptionsAreKeptAndOmittedOptionsAreTheCarefulDefault()
    {
        var custom = new RestoreOptions(TerminateActiveConnections: true, VerifyChecksum: false, VerifyBackup: false);
        Assert.Equal(custom, Operation(options: custom).Options);
        Assert.Equal(new RestoreOptions(false, true, true), RestoreOptions.Default);
        Assert.Equal(RestoreOptions.Default, Operation().Options);
    }

    [Fact]
    public void TheTargetIsStoredAsAValidatedCopy()
    {
        var operation = Operation();
        Assert.Equal(Target(), operation.Target);
        Assert.Equal("sql01.corp.local", operation.Target.Host);
    }

    [Fact]
    public void ConstructionRefusesEmptyIdentifiers()
    {
        RestoreOperation Build(Guid id, Guid artifactId, Guid replicaId) => new(id, TenantId.New(), artifactId, replicaId, RestoreSourceAccess.CentralStorage, AgentId.New(), Target(),
            "Sales", RestoreMode.NewDatabase, false, null, "key", "fp", null, Now);

        Assert.Throws<ArgumentException>(() => Build(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Build(Guid.NewGuid(), Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Build(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public void ConstructionRefusesMissingTenantAgentAndTarget()
    {
        Assert.Throws<ArgumentNullException>(() => new RestoreOperation(Guid.NewGuid(), null!, Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(), Target(),
            "Sales", RestoreMode.NewDatabase, false, null, "key", "fp", null, Now));
        Assert.Throws<ArgumentNullException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, null!, Target(),
            "Sales", RestoreMode.NewDatabase, false, null, "key", "fp", null, Now));
        Assert.Throws<ArgumentNullException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(), null!,
            "Sales", RestoreMode.NewDatabase, false, null, "key", "fp", null, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructionRefusesABlankIdempotencyKeyOrFingerprint(string blank)
    {
        Assert.Throws<ArgumentException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(), Target(),
            "Sales", RestoreMode.NewDatabase, false, null, blank, "fp", null, Now));
        Assert.Throws<ArgumentException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(), Target(),
            "Sales", RestoreMode.NewDatabase, false, null, "key", blank, null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void ConstructionRefusesAnUndefinedModeAndAnUndefinedSourceAccess(int value)
    {
        Assert.Throws<ArgumentException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(),
            Target(), "Sales", (RestoreMode)value, false, null, "key", "fp", null, Now));
        Assert.Throws<ArgumentException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), (RestoreSourceAccess)value, AgentId.New(),
            Target(), "Sales", RestoreMode.NewDatabase, false, null, "key", "fp", null, Now));
    }

    [Fact]
    public void ConstructionRefusesAnInvalidTarget()
    {
        Assert.Throws<ArgumentException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(),
            Target() with { Port = 0 }, "Sales", RestoreMode.NewDatabase, false, null, "key", "fp", null, Now));
    }

    [Fact]
    public void ConstructionRefusesAnInvalidOrSystemDatabaseName()
    {
        Assert.Throws<ArgumentException>(() => Operation(databaseName: ""));
        Assert.Throws<ArgumentException>(() => Operation(databaseName: "master"));
    }

    // ---- overwrite confirmation ----

    [Fact]
    public void ReplacingAnExistingDatabaseNeedsAnExplicitConfirmation()
    {
        RestoreOperation Build(RestoreMode mode, bool confirmed) => new(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(),
            Target(), "Sales", mode, confirmed, null, "key", "fp", null, Now);

        Assert.Throws<ArgumentException>(() => Build(RestoreMode.ReplaceExistingDatabase, false));
        var replace = Build(RestoreMode.ReplaceExistingDatabase, true);
        Assert.Equal(RestoreMode.ReplaceExistingDatabase, replace.Mode);
        Assert.Equal(RestoreOverwritePolicy.ReplaceExisting, replace.OverwritePolicy);
    }

    [Fact]
    public void ANewDatabaseRestoreRefusesAnOverwriteConfirmationAndForbidsOverwriting()
    {
        RestoreOperation Build(bool confirmed) => new(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(),
            Target(), "Sales", RestoreMode.NewDatabase, confirmed, null, "key", "fp", null, Now);

        Assert.Throws<ArgumentException>(() => Build(true));
        var created = Build(false);
        Assert.Equal(RestoreMode.NewDatabase, created.Mode);
        Assert.Equal(RestoreOverwritePolicy.Forbidden, created.OverwritePolicy);
    }

    // ---- database name ----

    public static TheoryData<string> AcceptedNames => new()
    {
        "Sales", "my db", "a", "Sales_2026-10", "Ünïcødé数据", "master2", "mastery", "with.dots", new string('x', 128)
    };

    public static TheoryData<string?> RejectedNames => new()
    {
        null, "", " ", "   ", new string('x', 129), " leading", "trailing ", "\ttab", "with\ttab", "line\nbreak", "nul\0char", "bell\a", " Sales "
    };

    public static TheoryData<string> SystemNames => new()
    {
        "master", "MASTER", "Master", "model", "MODEL", "msdb", "MsDb", "tempdb", "TempDB", "mssqlsystemresource", "MsSqlSystemResource"
    };

    [Theory]
    [MemberData(nameof(AcceptedNames))]
    public void ValidateDatabaseNameAcceptsOrdinaryNames(string name)
    {
        RestoreOperation.ValidateDatabaseName(name);
        Assert.Equal(name, Operation(databaseName: name).DatabaseName);
    }

    [Theory]
    [MemberData(nameof(RejectedNames))]
    public void ValidateDatabaseNameRejectsEmptyOverlongControlAndPaddedNames(string? name) =>
        Assert.Throws<ArgumentException>(() => RestoreOperation.ValidateDatabaseName(name));

    [Theory]
    [MemberData(nameof(SystemNames))]
    public void ValidateDatabaseNameRejectsSystemDatabasesWhateverTheCase(string name)
    {
        var failure = Assert.Throws<ArgumentException>(() => RestoreOperation.ValidateDatabaseName(name));
        Assert.Contains("system database", failure.Message);
    }

    [Fact]
    public void PostgreSqlNamesUseUtf8BytesAndTheirOwnProtectedDatabaseSet()
    {
        RestoreOperation.ValidateDatabaseName(new string('a', 63), "postgres");
        RestoreOperation.ValidateDatabaseName("master", "postgres");
        Assert.Throws<ArgumentException>(() => RestoreOperation.ValidateDatabaseName(new string('a', 64), "postgres"));
        Assert.Throws<ArgumentException>(() => RestoreOperation.ValidateDatabaseName(string.Concat(Enumerable.Repeat("数", 22)), "postgres"));
        foreach (var name in new[] { "postgres", "Postgres", "template0", "TEMPLATE1" })
            Assert.Throws<ArgumentException>(() => RestoreOperation.ValidateDatabaseName(name, "postgres"));
        RestoreOperation.ValidateDatabaseName("postgres", "mssql");
    }

    // ---- target validation ----

    [Fact]
    public void ATargetWithIntegratedSecurityAndNoCredentialIsValid()
    {
        var target = new RestoreTarget("db.internal", null, true, null, true, false);
        Assert.Same(target, target.Validate());
    }

    [Fact]
    public void ATargetWithACredentialReferenceAndNoIntegratedSecurityIsValid()
    {
        var target = new RestoreTarget("db.internal", 1433, false, "sql-prod_1", true, false);
        Assert.Same(target, target.Validate());
    }

    [Fact]
    public void ATargetMustUseExactlyOneOfIntegratedSecurityAndACredentialReference()
    {
        Assert.Throws<ArgumentException>(() => new RestoreTarget("db", null, true, "sql-cred", true, false).Validate());
        Assert.Throws<ArgumentException>(() => new RestoreTarget("db", null, false, null, true, false).Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void ATargetPortOutsideTheValidRangeIsRejected(int port) =>
        Assert.Throws<ArgumentException>(() => new RestoreTarget("db", port, true, null, true, false).Validate());

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(1433)]
    [InlineData(65535)]
    public void ATargetPortInsideTheValidRangeOrOmittedIsAccepted(int? port) =>
        Assert.Equal(port, new RestoreTarget("db", port, true, null, true, false).Validate().Port);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("quote'name")]
    [InlineData("at@name")]
    public void ACredentialReferenceOutsideTheReferenceCharsetIsRejected(string reference) =>
        Assert.Throws<ArgumentException>(() => new RestoreTarget("db", null, false, reference, true, false).Validate());

    [Theory]
    [InlineData("a")]
    [InlineData("sql-prod_1")]
    [InlineData("name.with.dots")]
    public void ACredentialReferenceInsideTheReferenceCharsetIsAccepted(string reference) =>
        Assert.Equal(reference, new RestoreTarget("db", null, false, reference, true, false).Validate().DatabaseCredentialReference);

    [Fact]
    public void ACredentialReferenceIsBoundedToSixtyFourCharacters()
    {
        Assert.Equal(64, new RestoreTarget("db", null, false, new string('a', 64), true, false).Validate().DatabaseCredentialReference!.Length);
        Assert.Throws<ArgumentException>(() => new RestoreTarget("db", null, false, new string('a', 65), true, false).Validate());
    }

    [Fact]
    public void ATargetHostMustBeNonBlankPrintableAndBounded()
    {
        Assert.Throws<ArgumentException>(() => new RestoreTarget("", null, true, null, true, false).Validate());
        Assert.Throws<ArgumentException>(() => new RestoreTarget("  ", null, true, null, true, false).Validate());
        Assert.Throws<ArgumentException>(() => new RestoreTarget("ho\nst", null, true, null, true, false).Validate());
        Assert.Throws<ArgumentException>(() => new RestoreTarget(new string('h', 256), null, true, null, true, false).Validate());
        Assert.Equal(255, new RestoreTarget(new string('h', 255), null, true, null, true, false).Validate().Host.Length);
    }

    // ---- first attempt ----

    [Fact]
    public void QueueFirstAttemptCreatesAPendingAttemptNumberOne()
    {
        var operation = Operation();
        var attemptId = Guid.NewGuid();
        var attempt = operation.QueueFirstAttempt(attemptId, Now);

        Assert.Equal(attemptId, attempt.Id);
        Assert.Equal(1, attempt.Number);
        Assert.Equal(RestoreStatus.Pending, attempt.Status);
        Assert.Equal(operation.Id, attempt.OperationId);
        Assert.Equal(operation.AgentId, attempt.AgentId);
        Assert.Equal(operation.TenantId, attempt.TenantId);
        Assert.Equal(Now, attempt.CreatedAt);
        Assert.Null(attempt.LeaseToken);
        Assert.Equal(1, operation.CurrentAttemptNumber);
        Assert.Same(attempt, operation.CurrentAttempt);
        Assert.Same(attempt, Assert.Single(operation.Attempts));
    }

    [Fact]
    public void ARestoreHasExactlyOneAttempt()
    {
        var operation = Queued();
        Assert.Throws<InvalidOperationException>(() => operation.QueueFirstAttempt(Guid.NewGuid(), Now));
        Assert.Single(operation.Attempts);
    }

    [Fact]
    public void QueueFirstAttemptRefusesAnEmptyAttemptIdWithoutChangingTheOperation()
    {
        var operation = Operation();
        Assert.Throws<ArgumentException>(() => operation.QueueFirstAttempt(Guid.Empty, Now));
        Assert.Empty(operation.Attempts);
        Assert.Equal(0, operation.CurrentAttemptNumber);
    }

    [Fact]
    public void WithoutAnAttemptThereIsNothingToClaim()
    {
        var operation = Operation();
        Assert.Throws<InvalidOperationException>(() => operation.Claim(Guid.NewGuid(), Now, Lease));
        Assert.Equal(RestoreStatus.Pending, operation.Status);
    }

    // ---- claim ----

    [Fact]
    public void ClaimingAPendingRestoreLeasesItToTheAgent()
    {
        var operation = Queued();
        var token = Guid.NewGuid();
        var at = Now.AddSeconds(5);
        operation.Claim(token, at, Lease);

        Assert.Equal(RestoreStatus.Leased, operation.Status);
        Assert.Equal(RestoreStatus.Leased, operation.CurrentAttempt.Status);
        Assert.Equal(token, operation.CurrentAttempt.LeaseToken);
        Assert.Equal(at, operation.CurrentAttempt.LeasedAt);
        Assert.Equal(at + Lease, operation.CurrentAttempt.LeaseExpiresAt);
        Assert.True(operation.CurrentAttempt.HasLease(token, at));
        Assert.True(operation.IsActive);
        Assert.True(operation.Pin.IsActive);
        Assert.Null(operation.StartedAt);
    }

    [Fact]
    public void OnlyAPendingRestoreCanBeClaimed()
    {
        var leased = Queued();
        Claim(leased);
        Assert.Throws<InvalidOperationException>(() => leased.Claim(Guid.NewGuid(), Now, Lease));

        var running = Queued();
        Begin(running);
        Assert.Throws<InvalidOperationException>(() => running.Claim(Guid.NewGuid(), Now, Lease));

        var cancelled = Queued();
        cancelled.RequestCancellation(Now);
        Assert.Throws<InvalidOperationException>(() => cancelled.Claim(Guid.NewGuid(), Now, Lease));
    }

    [Fact]
    public void ClaimRefusesANonPositiveLeaseAndAnEmptyTokenAndStaysPending()
    {
        var operation = Queued();
        Assert.Throws<InvalidOperationException>(() => operation.Claim(Guid.NewGuid(), Now, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => operation.Claim(Guid.NewGuid(), Now, -Lease));
        Assert.Throws<ArgumentException>(() => operation.Claim(Guid.Empty, Now, Lease));
        Assert.Equal(RestoreStatus.Pending, operation.Status);
        Assert.Equal(RestoreStatus.Pending, operation.CurrentAttempt.Status);
        Assert.Null(operation.CurrentAttempt.LeaseToken);
    }

    // ---- start ----

    [Fact]
    public void StartingALeasedRestoreMakesItRunning()
    {
        var operation = Queued();
        var token = Claim(operation);
        var at = Now.AddSeconds(3);
        operation.Start(token, at, "1.2.3", "1.0", "Linux");

        Assert.Equal(RestoreStatus.Running, operation.Status);
        Assert.Equal(RestoreStatus.Running, operation.CurrentAttempt.Status);
        Assert.Equal(at, operation.StartedAt);
        Assert.Equal(at, operation.CurrentAttempt.StartedAt);
        Assert.Equal("1.2.3", operation.CurrentAttempt.SoftwareVersion);
        Assert.Equal("1.0", operation.CurrentAttempt.ProtocolVersion);
        Assert.Equal("Linux", operation.CurrentAttempt.Platform);
        Assert.True(operation.Pin.IsActive);
    }

    [Fact]
    public void StartNeedsTheLeaseOfTheAttempt()
    {
        var pending = Queued();
        Assert.Throws<InvalidOperationException>(() => pending.Start(Guid.NewGuid(), Now, "1", "1", "Linux"));
        Assert.Equal(RestoreStatus.Pending, pending.Status);

        var leased = Queued();
        var token = Claim(leased);
        Assert.Throws<InvalidOperationException>(() => leased.Start(Guid.NewGuid(), Now, "1", "1", "Linux"));
        Assert.Throws<InvalidOperationException>(() => leased.Start(Guid.Empty, Now, "1", "1", "Linux"));
        Assert.Throws<InvalidOperationException>(() => leased.Start(token, Now + Lease, "1", "1", "Linux"));
        Assert.Throws<InvalidOperationException>(() => leased.Start(token, Now + Lease + Hour, "1", "1", "Linux"));
        Assert.Equal(RestoreStatus.Leased, leased.Status);
        Assert.Null(leased.StartedAt);

        leased.Start(token, Now + Lease - OneTick, "1", "1", "Linux");
        Assert.Equal(RestoreStatus.Running, leased.Status);
    }

    [Theory]
    [InlineData("", "1", "Linux")]
    [InlineData("1", "", "Linux")]
    [InlineData("1", "1", "")]
    [InlineData("   ", "1", "Linux")]
    public void StartRefusesBlankMetadata(string software, string protocol, string platform)
    {
        var operation = Queued();
        var token = Claim(operation);
        Assert.Throws<ArgumentException>(() => operation.Start(token, Now, software, protocol, platform));
        Assert.Equal(RestoreStatus.Leased, operation.Status);
    }

    [Fact]
    public void StartRefusesOverlongMetadata()
    {
        var operation = Queued();
        var token = Claim(operation);
        Assert.Throws<ArgumentException>(() => operation.Start(token, Now, new string('s', 65), "1", "Linux"));
        Assert.Throws<ArgumentException>(() => operation.Start(token, Now, "1", new string('p', 33), "Linux"));
        Assert.Throws<ArgumentException>(() => operation.Start(token, Now, "1", "1", new string('l', 33)));
        operation.Start(token, Now, new string('s', 64), new string('p', 32), new string('l', 32));
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    [Fact]
    public void RepeatingTheSameStartChangesNothing()
    {
        var operation = Queued();
        var token = Claim(operation);
        operation.Start(token, Now.AddSeconds(1), "1.0", "1.0", "Linux");
        operation.Start(token, Now.AddSeconds(30), "1.0", "1.0", "Linux");

        Assert.Equal(RestoreStatus.Running, operation.Status);
        Assert.Equal(Now.AddSeconds(1), operation.StartedAt);
        Assert.Equal(Now.AddSeconds(1), operation.CurrentAttempt.StartedAt);
    }

    [Theory]
    [InlineData("2.0", "1.0", "Linux")]
    [InlineData("1.0", "2.0", "Linux")]
    [InlineData("1.0", "1.0", "Windows")]
    public void RepeatingAStartWithDifferentMetadataIsRefused(string software, string protocol, string platform)
    {
        var operation = Queued();
        var token = Claim(operation);
        operation.Start(token, Now, "1.0", "1.0", "Linux");

        Assert.Throws<InvalidOperationException>(() => operation.Start(token, Now.AddSeconds(1), software, protocol, platform));
        Assert.Equal("1.0", operation.CurrentAttempt.SoftwareVersion);
        Assert.Equal("1.0", operation.CurrentAttempt.ProtocolVersion);
        Assert.Equal("Linux", operation.CurrentAttempt.Platform);
    }

    // ---- renew ----

    [Fact]
    public void RenewalExtendsTheLeaseFromNow()
    {
        var operation = Queued();
        var token = Claim(operation);
        var at = Now.AddSeconds(90);
        operation.Renew(token, at, Lease);

        Assert.Equal(at + Lease, operation.CurrentAttempt.LeaseExpiresAt);
        Assert.Equal(RestoreStatus.Leased, operation.Status);
    }

    [Fact]
    public void RenewalNeverShortensTheLease()
    {
        var operation = Queued();
        var token = Claim(operation);
        var original = operation.CurrentAttempt.LeaseExpiresAt;
        operation.Renew(token, Now.AddSeconds(1), TimeSpan.FromSeconds(10));
        Assert.Equal(original, operation.CurrentAttempt.LeaseExpiresAt);
    }

    [Fact]
    public void RenewalWorksForARunningRestoreToo()
    {
        var operation = Queued();
        var token = Begin(operation);
        operation.Renew(token, Now.AddSeconds(100), Lease);
        Assert.Equal(Now.AddSeconds(100) + Lease, operation.CurrentAttempt.LeaseExpiresAt);
    }

    [Fact]
    public void RenewalRefusesANonPositiveDuration()
    {
        var operation = Queued();
        var token = Claim(operation);
        Assert.Throws<ArgumentException>(() => operation.Renew(token, Now, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => operation.Renew(token, Now, -Lease));
    }

    [Fact]
    public void RenewalNeedsALiveLeaseHeldByTheCaller()
    {
        var operation = Queued();
        var token = Claim(operation);
        Assert.Throws<InvalidOperationException>(() => operation.Renew(Guid.NewGuid(), Now, Lease));
        Assert.Throws<InvalidOperationException>(() => operation.Renew(token, Now + Lease, Lease));
        Assert.Equal(Now + Lease, operation.CurrentAttempt.LeaseExpiresAt);

        var pending = Queued();
        Assert.Throws<InvalidOperationException>(() => pending.Renew(Guid.NewGuid(), Now, Lease));
    }

    [Fact]
    public void ATerminalRestoreCannotBeRenewed()
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(10), Success());
        Assert.Throws<InvalidOperationException>(() => operation.Renew(token, Now.AddSeconds(11), Lease));
    }

    // ---- progress ----

    public static TheoryData<string> ReportableStages => new()
    {
        RestoreStages.Preparing, RestoreStages.DownloadingArtifact, RestoreStages.VerifyingChecksum, RestoreStages.InspectingBackup,
        RestoreStages.PreparingTarget, RestoreStages.Restoring, RestoreStages.Finalizing
    };

    [Theory]
    [MemberData(nameof(ReportableStages))]
    public void ARunningRestoreRecordsEveryKnownNonTerminalStage(string stage)
    {
        var operation = Running(out var token);
        var at = Now.AddSeconds(5);
        operation.Progress(token, at, stage, 12.5);

        Assert.Equal(stage, operation.CurrentAttempt.ProgressStage);
        Assert.Equal(12.5, operation.CurrentAttempt.ProgressPercent);
        Assert.Equal(at, operation.CurrentAttempt.ProgressAt);
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    [Fact]
    public void ProgressIsOnlyAcceptedWhileRunning()
    {
        var operation = Queued();
        var token = Claim(operation);
        Assert.Throws<InvalidOperationException>(() => operation.Progress(token, Now, RestoreStages.Preparing, 1));
        Assert.Null(operation.CurrentAttempt.ProgressStage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nope")]
    [InlineData("restoring")]
    [InlineData(RestoreStages.Completed)]
    [InlineData(RestoreStages.Failed)]
    [InlineData(RestoreStages.Cancelled)]
    public void UnknownAndTerminalStagesAreRefused(string stage)
    {
        var operation = Running(out var token);
        Assert.Throws<ArgumentException>(() => operation.Progress(token, Now.AddSeconds(1), stage, 10));
        Assert.Null(operation.CurrentAttempt.ProgressStage);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void APercentageOutsideZeroToOneHundredIsRefused(double percent)
    {
        var operation = Running(out var token);
        Assert.Throws<ArgumentException>(() => operation.Progress(token, Now.AddSeconds(1), RestoreStages.Restoring, percent));
        Assert.Null(operation.CurrentAttempt.ProgressPercent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(100.0)]
    public void ThePercentageBoundsAndAnUnknownPercentageAreAccepted(double? percent)
    {
        var operation = Running(out var token);
        operation.Progress(token, Now.AddSeconds(1), RestoreStages.Restoring, percent);
        Assert.Equal(percent, operation.CurrentAttempt.ProgressPercent);
    }

    [Fact]
    public void OlderProgressDoesNotOverwriteNewerProgress()
    {
        var operation = Running(out var token);
        operation.Progress(token, Now.AddSeconds(20), RestoreStages.Restoring, 60);
        operation.Progress(token, Now.AddSeconds(10), RestoreStages.VerifyingChecksum, 5);

        Assert.Equal(RestoreStages.Restoring, operation.CurrentAttempt.ProgressStage);
        Assert.Equal(60.0, operation.CurrentAttempt.ProgressPercent);
        Assert.Equal(Now.AddSeconds(20), operation.CurrentAttempt.ProgressAt);
    }

    [Fact]
    public void ProgressAtTheSameInstantReplacesThePreviousReport()
    {
        var operation = Running(out var token);
        operation.Progress(token, Now.AddSeconds(10), RestoreStages.Restoring, 10);
        operation.Progress(token, Now.AddSeconds(10), RestoreStages.Restoring, 20);
        Assert.Equal(20.0, operation.CurrentAttempt.ProgressPercent);
    }

    [Fact]
    public void ProgressNeedsALiveLeaseHeldByTheCaller()
    {
        var operation = Running(out var token);
        Assert.Throws<InvalidOperationException>(() => operation.Progress(Guid.NewGuid(), Now.AddSeconds(1), RestoreStages.Restoring, 1));
        Assert.Throws<InvalidOperationException>(() => operation.Progress(token, Now + Lease, RestoreStages.Restoring, 1));
        Assert.Null(operation.CurrentAttempt.ProgressStage);
    }

    // ---- completion ----

    [Fact]
    public void ASuccessfulCompletionEndsTheRestoreOnlineAndReleasesThePin()
    {
        var operation = Running(out var token);
        var at = Now.AddMinutes(1);
        operation.Complete(token, at, Success("{\"checksumVerified\":true}"));

        Assert.Equal(RestoreStatus.Succeeded, operation.Status);
        Assert.Equal(RestoreTargetState.Online, operation.TargetState);
        Assert.Null(operation.FailureCode);
        Assert.Equal("{\"checksumVerified\":true}", operation.ResultJson);
        AssertPinReleasedAt(operation, at);
        var attempt = operation.CurrentAttempt;
        Assert.Equal(RestoreStatus.Succeeded, attempt.Status);
        Assert.Equal(RestoreTargetState.Online, attempt.TargetState);
        Assert.Equal(at, attempt.CompletedAt);
        Assert.Equal(RestoreStages.Completed, attempt.ProgressStage);
        Assert.Equal(100.0, attempt.ProgressPercent);
        Assert.False(attempt.HasLease(token, at));
    }

    [Fact]
    public void AFailedCompletionRecordsTheCodeAndTheTargetStateAndReleasesThePin()
    {
        var operation = Running(out var token);
        var at = Now.AddMinutes(1);
        operation.Complete(token, at, Failure(RestoreFailureCodes.InsufficientDiskSpace, RestoreTargetState.NotModified));

        Assert.Equal(RestoreStatus.Failed, operation.Status);
        Assert.Equal(RestoreFailureCodes.InsufficientDiskSpace, operation.FailureCode);
        Assert.Equal(RestoreTargetState.NotModified, operation.TargetState);
        Assert.Null(operation.ResultJson);
        Assert.Equal(RestoreStages.Failed, operation.CurrentAttempt.ProgressStage);
        AssertPinReleasedAt(operation, at);
    }

    [Fact]
    public void ACancelledCompletionByTheAgentRecordsWhatWasLeftOnTheTarget()
    {
        var operation = Running(out var token);
        operation.RequestCancellation(Now.AddSeconds(5));
        var at = Now.AddSeconds(20);
        operation.Complete(token, at, new RestoreCompletion(RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.PartiallyRestored, null));

        Assert.Equal(RestoreStatus.Cancelled, operation.Status);
        Assert.Equal(RestoreFailureCodes.Cancelled, operation.FailureCode);
        Assert.Equal(RestoreTargetState.PartiallyRestored, operation.TargetState);
        Assert.Equal(RestoreStages.Cancelled, operation.CurrentAttempt.ProgressStage);
        Assert.Equal(Now.AddSeconds(5), operation.CancellationRequestedAt);
        AssertPinReleasedAt(operation, at);
    }

    [Fact]
    public void ACancelledCompletionWithoutACodeKeepsTheCodeNull()
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(10), new RestoreCompletion(RestoreStatus.Cancelled, null, RestoreTargetState.NotModified, null));
        Assert.Equal(RestoreStatus.Cancelled, operation.Status);
        Assert.Null(operation.FailureCode);
    }

    [Fact]
    public void AFailureCanBeReportedByAnAttemptThatNeverStarted()
    {
        var operation = Queued();
        var token = Claim(operation);
        operation.Complete(token, Now.AddSeconds(5), Failure(RestoreFailureCodes.StagingInvalid, RestoreTargetState.NotModified));
        Assert.Equal(RestoreStatus.Failed, operation.Status);
        Assert.Null(operation.StartedAt);
    }

    [Fact]
    public void ASuccessNeedsARunningAttempt()
    {
        var operation = Queued();
        var token = Claim(operation);
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(1), Success()));
        Assert.Equal(RestoreStatus.Leased, operation.Status);
        Assert.True(operation.Pin.IsActive);
    }

    [Fact]
    public void CompletionNeedsTheLeaseOfTheAttempt()
    {
        var operation = Running(out var token);
        Assert.Throws<InvalidOperationException>(() => operation.Complete(Guid.NewGuid(), Now.AddSeconds(1), Success()));
        Assert.Throws<InvalidOperationException>(() => operation.Complete(Guid.Empty, Now.AddSeconds(1), Success()));
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now + Lease, Success()));
        Assert.Equal(RestoreStatus.Running, operation.Status);
        Assert.True(operation.Pin.IsActive);
        Assert.Null(operation.CompletedAt);
    }

    [Fact]
    public void CompletionRefusesANullCompletion()
    {
        var operation = Running(out var token);
        Assert.Throws<ArgumentNullException>(() => operation.Complete(token, Now.AddSeconds(1), null!));
    }

    [Fact]
    public void CompletingTheSameOutcomeTwiceChangesNothing()
    {
        var operation = Running(out var token);
        var completion = Success("{\"databaseState\":\"ONLINE\"}");
        operation.Complete(token, Now.AddSeconds(30), completion);
        var pinReleasedAt = operation.Pin.ReleasedAt;

        operation.Complete(token, Now.AddMinutes(10), completion with { });

        Assert.Equal(RestoreStatus.Succeeded, operation.Status);
        Assert.Equal(Now.AddSeconds(30), operation.CompletedAt);
        Assert.Equal(Now.AddSeconds(30), operation.CurrentAttempt.CompletedAt);
        Assert.Equal(pinReleasedAt, operation.Pin.ReleasedAt);
        Assert.Equal("{\"databaseState\":\"ONLINE\"}", operation.ResultJson);
    }

    [Fact]
    public void CompletingAFailureTwiceIsAlsoIdempotent()
    {
        var operation = Running(out var token);
        var completion = Failure(RestoreFailureCodes.TargetInUse, RestoreTargetState.NotModified);
        operation.Complete(token, Now.AddSeconds(30), completion);
        operation.Complete(token, Now.AddSeconds(31), completion);
        Assert.Equal(RestoreStatus.Failed, operation.Status);
        Assert.Equal(Now.AddSeconds(30), operation.CompletedAt);
    }

    [Fact]
    public void ADifferentCompletionAfterTheTerminalOutcomeIsRefused()
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(30), Failure(RestoreFailureCodes.TargetInUse, RestoreTargetState.NotModified));

        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(31), Success()));
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(31), Failure(RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState.NotModified)));
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(31), Failure(RestoreFailureCodes.TargetInUse, RestoreTargetState.Unknown)));
        Assert.Equal(RestoreStatus.Failed, operation.Status);
        Assert.Equal(RestoreFailureCodes.TargetInUse, operation.FailureCode);
        Assert.Equal(RestoreTargetState.NotModified, operation.TargetState);
    }

    [Fact]
    public void TheSameOutcomeWithADifferentResultAfterTheTerminalOutcomeIsRefused()
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(30), Success());
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(31), Success("{}")));
        Assert.Null(operation.ResultJson);
    }

    [Fact]
    public void ARepeatedCompletionUnderAnotherTokenIsRefused()
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(30), Success());
        Assert.Throws<InvalidOperationException>(() => operation.Complete(Guid.NewGuid(), Now.AddSeconds(31), Success()));
    }

    [Fact]
    public void ResultsUpToTheMaximumLengthAreStoredAndLongerOnesRefused()
    {
        var accepted = Running(out var token);
        var atLimit = new string('x', RestoreOperation.MaxResultJsonLength);
        accepted.Complete(token, Now.AddSeconds(1), Success(atLimit));
        Assert.Equal(RestoreOperation.MaxResultJsonLength, accepted.ResultJson!.Length);

        var rejected = Running(out var otherToken);
        Assert.Throws<ArgumentException>(() => rejected.Complete(otherToken, Now.AddSeconds(1), Success(new string('x', RestoreOperation.MaxResultJsonLength + 1))));
        Assert.Equal(RestoreStatus.Running, rejected.Status);
        Assert.True(rejected.Pin.IsActive);
    }

    [Fact]
    public void ARejectedCompletionLeavesTheAttemptRunnableAndCompletableLater()
    {
        var operation = Running(out var token);
        Assert.Throws<ArgumentException>(() => operation.Complete(token, Now.AddSeconds(1), Failure(RestoreFailureCodes.Cancelled, RestoreTargetState.NotModified)));
        Assert.Equal(RestoreStatus.Running, operation.Status);
        operation.Complete(token, Now.AddSeconds(2), Failure(RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState.PartiallyRestored));
        Assert.Equal(RestoreStatus.Failed, operation.Status);
    }

    // ---- cancellation ----

    [Fact]
    public void CancellingAPendingRestoreWithdrawsItAtOnce()
    {
        var operation = Queued();
        var at = Now.AddSeconds(30);
        operation.RequestCancellation(at);

        Assert.Equal(RestoreStatus.Cancelled, operation.Status);
        Assert.Equal(RestoreFailureCodes.Cancelled, operation.FailureCode);
        Assert.Equal(RestoreTargetState.NotModified, operation.TargetState);
        Assert.Equal(at, operation.CancellationRequestedAt);
        Assert.Null(operation.ResultJson);
        Assert.Equal(RestoreStatus.Cancelled, operation.CurrentAttempt.Status);
        Assert.Equal(RestoreFailureCodes.Cancelled, operation.CurrentAttempt.FailureCode);
        Assert.Equal(RestoreTargetState.NotModified, operation.CurrentAttempt.TargetState);
        Assert.Equal(RestoreStages.Cancelled, operation.CurrentAttempt.ProgressStage);
        AssertPinReleasedAt(operation, at);
    }

    [Fact]
    public void ACancelledPendingRestoreCannotBeCancelledAgainOrChangedByAClaim()
    {
        var operation = Queued();
        operation.RequestCancellation(Now.AddSeconds(30));
        operation.RequestCancellation(Now.AddSeconds(60));

        Assert.Equal(Now.AddSeconds(30), operation.CancellationRequestedAt);
        Assert.Equal(Now.AddSeconds(30), operation.CompletedAt);
        Assert.Throws<InvalidOperationException>(() => operation.Claim(Guid.NewGuid(), Now.AddSeconds(61), Lease));
    }

    [Fact]
    public void CancellingALeasedRestoreOnlyFlagsItAndTheAgentStillOwnsTheOutcome()
    {
        var operation = Queued();
        var token = Claim(operation);
        var at = Now.AddSeconds(15);
        operation.RequestCancellation(at);

        Assert.Equal(at, operation.CancellationRequestedAt);
        Assert.Equal(RestoreStatus.Leased, operation.Status);
        Assert.True(operation.IsActive);
        Assert.True(operation.Pin.IsActive);
        Assert.Null(operation.CompletedAt);
        Assert.Null(operation.TargetState);
        Assert.True(operation.CurrentAttempt.HasLease(token, at));
    }

    [Fact]
    public void CancellingARunningRestoreOnlyFlagsItAndKeepsThePin()
    {
        var operation = Running(out var token);
        operation.Progress(token, Now.AddSeconds(5), RestoreStages.Restoring, 40);
        operation.RequestCancellation(Now.AddSeconds(10));

        Assert.Equal(Now.AddSeconds(10), operation.CancellationRequestedAt);
        Assert.Equal(RestoreStatus.Running, operation.Status);
        Assert.Equal(RestoreStatus.Running, operation.CurrentAttempt.Status);
        Assert.True(operation.IsActive);
        Assert.True(operation.Pin.IsActive);
        Assert.Equal(RestoreStages.Restoring, operation.CurrentAttempt.ProgressStage);
        Assert.Null(operation.FailureCode);
    }

    [Fact]
    public void RepeatingACancellationRequestKeepsTheFirstTimestamp()
    {
        var operation = Running(out _);
        operation.RequestCancellation(Now.AddSeconds(10));
        operation.RequestCancellation(Now.AddSeconds(20));
        Assert.Equal(Now.AddSeconds(10), operation.CancellationRequestedAt);
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    [Fact]
    public void ARunningRestoreStillCompletesNormallyAfterACancellationRequest()
    {
        var operation = Running(out var token);
        operation.RequestCancellation(Now.AddSeconds(10));
        operation.Complete(token, Now.AddSeconds(20), Failure(RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState.PartiallyRestored));
        Assert.Equal(RestoreStatus.Failed, operation.Status);
        Assert.Equal(Now.AddSeconds(10), operation.CancellationRequestedAt);
    }

    [Fact]
    public void CancellingATerminalRestoreChangesNothing()
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(10), Success());
        operation.RequestCancellation(Now.AddSeconds(20));

        Assert.Equal(RestoreStatus.Succeeded, operation.Status);
        Assert.Null(operation.CancellationRequestedAt);
        Assert.Equal(Now.AddSeconds(10), operation.CompletedAt);
        Assert.Equal(RestoreTargetState.Online, operation.TargetState);

        var failed = Running(out var failedToken);
        failed.Complete(failedToken, Now.AddSeconds(10), Failure());
        failed.RequestCancellation(Now.AddSeconds(20));
        Assert.Equal(RestoreStatus.Failed, failed.Status);
        Assert.Null(failed.CancellationRequestedAt);
    }

    // ---- expiry ----

    [Fact]
    public void ALeasedRestoreWhoseLeaseRanOutIsAgentLostAndTheTargetUntouched()
    {
        var operation = Queued();
        Claim(operation);
        var at = Now + Lease;

        Assert.True(operation.Expire(at));

        Assert.Equal(RestoreStatus.AgentLost, operation.Status);
        Assert.Equal(RestoreFailureCodes.AgentLost, operation.FailureCode);
        Assert.Equal(RestoreTargetState.NotModified, operation.TargetState);
        Assert.Equal(RestoreStatus.AgentLost, operation.CurrentAttempt.Status);
        Assert.Equal(RestoreStages.Failed, operation.CurrentAttempt.ProgressStage);
        Assert.Null(operation.ResultJson);
        AssertPinReleasedAt(operation, at);
    }

    [Fact]
    public void ALeaseIsNotExpiredOneTickBeforeItsExpiryAndExpiredExactlyAtIt()
    {
        var operation = Queued();
        Claim(operation);

        Assert.False(operation.Expire(Now + Lease - OneTick));
        Assert.Equal(RestoreStatus.Leased, operation.Status);
        Assert.True(operation.Pin.IsActive);
        Assert.Null(operation.CompletedAt);

        Assert.True(operation.Expire(Now + Lease));
        Assert.Equal(RestoreStatus.AgentLost, operation.Status);
    }

    [Fact]
    public void ARenewedLeaseDoesNotExpireAtItsOriginalExpiry()
    {
        var operation = Queued();
        var token = Claim(operation);
        operation.Renew(token, Now.AddSeconds(100), Lease);

        Assert.False(operation.Expire(Now + Lease));
        Assert.True(operation.Expire(Now.AddSeconds(100) + Lease));
    }

    [Fact]
    public void ARunningRestoreThatNeverReportedAStageMayHaveChangedTheTarget()
    {
        var operation = Running(out _);
        Assert.True(operation.Expire(Now + Lease));
        Assert.Equal(RestoreStatus.AgentLost, operation.Status);
        Assert.Equal(RestoreTargetState.Unknown, operation.TargetState);
        Assert.Equal(RestoreFailureCodes.AgentLost, operation.FailureCode);
    }

    [Fact]
    public void LostRunningLeaseCannotUseStaleEarlyProgressAsProofTheTargetIsUntouched()
    {
        var operation = Running(out var token);
        operation.Progress(token, Now.AddSeconds(5), RestoreStages.DownloadingArtifact, 30);
        // Progress is best effort; the agent may have started RESTORE without delivering another report.
        Assert.True(operation.Expire(Now + Lease));
        Assert.Equal(RestoreTargetState.Unknown, operation.TargetState);
        Assert.Equal(RestoreTargetState.Unknown, operation.CurrentAttempt.TargetState);
    }


    [Theory]
    [InlineData(RestoreStages.PreparingTarget)]
    [InlineData(RestoreStages.Restoring)]
    [InlineData(RestoreStages.Finalizing)]
    public void ARunningRestoreLostAtOrAfterTheTargetStagesIsInAnUnknownState(string stage)
    {
        var operation = Running(out var token);
        operation.Progress(token, Now.AddSeconds(5), stage, 30);
        Assert.True(operation.Expire(Now + Lease));
        Assert.Equal(RestoreTargetState.Unknown, operation.TargetState);
        Assert.Equal(RestoreStatus.AgentLost, operation.Status);
    }

    [Fact]
    public void ExpiryDoesNothingForPendingAndTerminalRestores()
    {
        var pending = Queued();
        Assert.False(pending.Expire(Now.AddDays(30)));
        Assert.Equal(RestoreStatus.Pending, pending.Status);
        Assert.True(pending.Pin.IsActive);

        var done = Running(out var token);
        done.Complete(token, Now.AddSeconds(10), Success());
        Assert.False(done.Expire(Now.AddDays(30)));
        Assert.Equal(RestoreStatus.Succeeded, done.Status);
        Assert.Equal(Now.AddSeconds(10), done.CompletedAt);

        var lost = Queued();
        Claim(lost);
        Assert.True(lost.Expire(Now + Lease));
        Assert.False(lost.Expire(Now + Lease + Hour));
        Assert.Equal(Now + Lease, lost.CompletedAt);
    }

    [Fact]
    public void ALateCompletionAfterTheLeaseWasExpiredIsRefused()
    {
        var operation = Running(out var token);
        operation.Expire(Now + Lease);
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now + Lease + OneTick, Success()));
        Assert.Equal(RestoreStatus.AgentLost, operation.Status);
    }

    // ---- unclaimed ----

    [Fact]
    public void APendingRestoreNobodyClaimedIsWithdrawnAfterTheMaximumWait()
    {
        var operation = Queued();
        var at = Now + Hour + TimeSpan.FromMinutes(5);

        Assert.True(operation.ExpireUnclaimed(at, Hour));

        Assert.Equal(RestoreStatus.Cancelled, operation.Status);
        Assert.Equal(RestoreFailureCodes.NotClaimed, operation.FailureCode);
        Assert.Equal(RestoreTargetState.NotModified, operation.TargetState);
        Assert.Equal(RestoreStatus.Cancelled, operation.CurrentAttempt.Status);
        Assert.Equal(RestoreFailureCodes.NotClaimed, operation.CurrentAttempt.FailureCode);
        Assert.Equal(RestoreTargetState.NotModified, operation.CurrentAttempt.TargetState);
        Assert.Null(operation.CancellationRequestedAt);
        AssertPinReleasedAt(operation, at);
    }

    [Fact]
    public void TheUnclaimedBoundaryIsExactlyCreatedAtPlusTheMaximumWait()
    {
        var operation = Queued();

        Assert.False(operation.ExpireUnclaimed(Now + Hour - OneTick, Hour));
        Assert.Equal(RestoreStatus.Pending, operation.Status);
        Assert.True(operation.Pin.IsActive);

        Assert.True(operation.ExpireUnclaimed(Now + Hour, Hour));
        Assert.Equal(RestoreStatus.Cancelled, operation.Status);
    }

    [Fact]
    public void OnlyAPendingRestoreCanExpireAsUnclaimed()
    {
        var leased = Queued();
        Claim(leased);
        Assert.False(leased.ExpireUnclaimed(Now.AddDays(10), Hour));
        Assert.Equal(RestoreStatus.Leased, leased.Status);

        var running = Queued();
        Begin(running);
        Assert.False(running.ExpireUnclaimed(Now.AddDays(10), Hour));
        Assert.Equal(RestoreStatus.Running, running.Status);

        var cancelled = Queued();
        cancelled.RequestCancellation(Now.AddSeconds(1));
        Assert.False(cancelled.ExpireUnclaimed(Now.AddDays(10), Hour));
        Assert.Equal(RestoreFailureCodes.Cancelled, cancelled.FailureCode);

        var done = Running(out var token);
        done.Complete(token, Now.AddSeconds(5), Success());
        Assert.False(done.ExpireUnclaimed(Now.AddDays(10), Hour));
        Assert.Equal(RestoreStatus.Succeeded, done.Status);
    }

    // ---- the pin follows the lifecycle ----

    [Fact]
    public void ThePinIsActiveFromCreationThroughEveryActiveState()
    {
        var operation = Operation();
        Assert.True(operation.Pin.IsActive);
        operation.QueueFirstAttempt(Guid.NewGuid(), Now);
        Assert.True(operation.Pin.IsActive);
        var token = Claim(operation);
        Assert.True(operation.Pin.IsActive);
        operation.Start(token, Now, "1", "1", "Linux");
        Assert.True(operation.Pin.IsActive);
        operation.Progress(token, Now.AddSeconds(1), RestoreStages.Restoring, 10);
        operation.Renew(token, Now.AddSeconds(2), Lease);
        operation.RequestCancellation(Now.AddSeconds(3));
        Assert.True(operation.Pin.IsActive);
        Assert.Null(operation.Pin.ReleasedAt);
    }

    [Fact]
    public void ThePinIsReleasedByEveryTerminalPath()
    {
        var succeeded = Running(out var successToken);
        succeeded.Complete(successToken, Now.AddSeconds(1), Success());
        AssertPinReleasedAt(succeeded, Now.AddSeconds(1));

        var failed = Running(out var failToken);
        failed.Complete(failToken, Now.AddSeconds(2), Failure());
        AssertPinReleasedAt(failed, Now.AddSeconds(2));

        var cancelledByAgent = Running(out var cancelToken);
        cancelledByAgent.RequestCancellation(Now.AddSeconds(1));
        cancelledByAgent.Complete(cancelToken, Now.AddSeconds(3), new RestoreCompletion(RestoreStatus.Cancelled, null, RestoreTargetState.NotModified, null));
        AssertPinReleasedAt(cancelledByAgent, Now.AddSeconds(3));

        var cancelledPending = Queued();
        cancelledPending.RequestCancellation(Now.AddSeconds(4));
        AssertPinReleasedAt(cancelledPending, Now.AddSeconds(4));

        var expired = Running(out _);
        expired.Expire(Now + Lease);
        AssertPinReleasedAt(expired, Now + Lease);

        var unclaimed = Queued();
        unclaimed.ExpireUnclaimed(Now + Hour, Hour);
        AssertPinReleasedAt(unclaimed, Now + Hour);
    }

    [Fact]
    public void ARepeatedTerminalCallNeverMovesTheReleaseTimeOfThePin()
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(1), Success());
        operation.Complete(token, Now.AddSeconds(50), Success());
        operation.RequestCancellation(Now.AddSeconds(60));
        operation.Expire(Now.AddDays(1));
        Assert.Equal(Now.AddSeconds(1), operation.Pin.ReleasedAt);
    }
}
