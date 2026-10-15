using System.Text.Json;
using System.Text.RegularExpressions;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Restores;

namespace BackupPlatform.UnitTests;

/// <summary>Boundary validation of the user's start request and of what the target agent reports.</summary>
public sealed class RestoreInputValidationTests
{
    private static readonly Guid ArtifactId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentGuid = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid ReplicaGuid = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Token = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset At = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static StartRestoreRequest Base { get; } = new(AgentGuid, new RestoreTargetRequest("sql01.corp.local", 1433, false, "sql-cred_1"), "Sales_Restored");

    private static StartRestoreRequest WithTarget(Func<RestoreTargetRequest, RestoreTargetRequest> change) => Base with { Target = change(Base.Target) };

    private static InvalidRestoreRequestException Rejected(StartRestoreRequest request, Guid? artifactId = null) =>
        Assert.Throws<InvalidRestoreRequestException>(() => RestoreInputValidation.Validate(artifactId ?? ArtifactId, request));

    private static string Fingerprint(StartRestoreRequest request, Guid? artifactId = null) => RestoreInputValidation.Validate(artifactId ?? ArtifactId, request).Fingerprint;

    // ---- defaults and modes ----

    [Fact]
    public void AMinimalRequestTakesTheCarefulDefaults()
    {
        var validated = RestoreInputValidation.Validate(ArtifactId, Base);

        Assert.Equal(RestoreMode.NewDatabase, validated.Mode);
        Assert.False(validated.OverwriteConfirmed);
        Assert.Equal(new RestoreOptions(TerminateActiveConnections: false, VerifyChecksum: true, VerifyBackup: true), validated.Options);
        Assert.Equal("Sales_Restored", validated.DatabaseName);
        Assert.Equal(new RestoreTarget("sql01.corp.local", 1433, false, "sql-cred_1", true, false), validated.Target);
    }

    [Fact]
    public void TargetSecurityFlagsDefaultToEncryptedAndNotTrusted()
    {
        var target = RestoreInputValidation.Validate(ArtifactId, Base).Target;
        Assert.True(target.Encrypt);
        Assert.False(target.TrustServerCertificate);
        var changed = RestoreInputValidation.Validate(ArtifactId, WithTarget(t => t with { Encrypt = false, TrustServerCertificate = true })).Target;
        Assert.False(changed.Encrypt);
        Assert.True(changed.TrustServerCertificate);
    }

    [Fact]
    public void ExplicitOptionsAreCarriedOverIndividually()
    {
        var validated = RestoreInputValidation.Validate(ArtifactId, Base with { Options = new RestoreOptionsRequest(TerminateActiveConnections: true) });
        Assert.Equal(new RestoreOptions(true, true, true), validated.Options);

        validated = RestoreInputValidation.Validate(ArtifactId, Base with { Options = new RestoreOptionsRequest(VerifyChecksum: false) });
        Assert.Equal(new RestoreOptions(false, false, true), validated.Options);

        validated = RestoreInputValidation.Validate(ArtifactId, Base with { Options = new RestoreOptionsRequest(VerifyBackup: false) });
        Assert.Equal(new RestoreOptions(false, true, false), validated.Options);

        validated = RestoreInputValidation.Validate(ArtifactId, Base with { Options = new RestoreOptionsRequest(true, false, false) });
        Assert.Equal(new RestoreOptions(true, false, false), validated.Options);
    }

    [Fact]
    public void ReplaceWithTheExactDatabaseNameIsAConfirmedOverwrite()
    {
        var validated = RestoreInputValidation.Validate(ArtifactId, Base with { Mode = "ReplaceExistingDatabase", ConfirmOverwrite = "Sales_Restored" });
        Assert.Equal(RestoreMode.ReplaceExistingDatabase, validated.Mode);
        Assert.True(validated.OverwriteConfirmed);
    }

    [Theory]
    [InlineData("replaceexistingdatabase")]
    [InlineData("REPLACEEXISTINGDATABASE")]
    [InlineData("ReplaceExistingDatabase")]
    public void ModeNamesAreCaseInsensitive(string mode)
    {
        var validated = RestoreInputValidation.Validate(ArtifactId, Base with { Mode = mode, ConfirmOverwrite = "Sales_Restored" });
        Assert.Equal(RestoreMode.ReplaceExistingDatabase, validated.Mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NewDatabase")]
    [InlineData("newdatabase")]
    [InlineData("NEWDATABASE")]
    public void ANewDatabaseModeIsTheDefaultAndCanBeNamedInAnyCase(string? mode)
    {
        var validated = RestoreInputValidation.Validate(ArtifactId, Base with { Mode = mode });
        Assert.Equal(RestoreMode.NewDatabase, validated.Mode);
        Assert.False(validated.OverwriteConfirmed);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("Replace")]
    [InlineData("New")]
    [InlineData(" NewDatabase")]
    [InlineData("NewDatabase ")]
    [InlineData("NewDatabase,ReplaceExistingDatabase")]
    [InlineData("Nope")]
    public void NumericTextAndUnknownModeNamesAreRejected(string mode)
    {
        var failure = Rejected(Base with { Mode = mode });
        Assert.Equal("mode", failure.Field);
        Assert.Equal("invalid_value", failure.Code);
    }

    [Fact]
    public void ReplaceWithoutAConfirmationIsRejectedAsConfirmationRequired()
    {
        var failure = Rejected(Base with { Mode = "ReplaceExistingDatabase" });
        Assert.Equal("confirmOverwrite", failure.Field);
        Assert.Equal("confirmation_required", failure.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sales_restored")]
    [InlineData("Sales_Restored ")]
    [InlineData(" Sales_Restored")]
    [InlineData("Other")]
    public void ReplaceWithAnythingButTheExactNameIsAMismatch(string confirmation)
    {
        var failure = Rejected(Base with { Mode = "ReplaceExistingDatabase", ConfirmOverwrite = confirmation });
        Assert.Equal("confirmOverwrite", failure.Field);
        Assert.Equal("confirmation_mismatch", failure.Code);
    }

    [Theory]
    [InlineData("Sales_Restored")]
    [InlineData("")]
    [InlineData("anything")]
    public void AConfirmationOnANewDatabaseRestoreIsNotApplicable(string confirmation)
    {
        foreach (var mode in new string?[] { null, "NewDatabase" })
        {
            var failure = Rejected(Base with { Mode = mode, ConfirmOverwrite = confirmation });
            Assert.Equal("confirmOverwrite", failure.Field);
            Assert.Equal("not_applicable", failure.Code);
        }
    }

    // ---- target ----

    [Theory]
    [InlineData("sql01")]
    [InlineData("10.0.0.5")]
    [InlineData("db-host_1.example.com")]
    [InlineData("host\\INSTANCE")]
    [InlineData("host:1433")]
    public void HostsMadeOfLettersDigitsAndTheConnectionPunctuationAreAccepted(string host)
    {
        Assert.Equal(host, RestoreInputValidation.Validate(ArtifactId, WithTarget(t => t with { Host = host })).Target.Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host name")]
    [InlineData("host/path")]
    [InlineData("host'quote")]
    [InlineData("host\nname")]
    [InlineData("hôst")]
    [InlineData("host,1433")]
    public void HostsOutsideTheConnectionVocabularyAreRejected(string host)
    {
        var failure = Rejected(WithTarget(t => t with { Host = host }));
        Assert.Equal("target.host", failure.Field);
        Assert.Equal("invalid_value", failure.Code);
    }

    [Fact]
    public void AHostIsBoundedToTwoHundredFiftyThreeCharacters()
    {
        Assert.Equal(253, RestoreInputValidation.Validate(ArtifactId, WithTarget(t => t with { Host = new string('h', 253) })).Target.Host.Length);
        Assert.Equal("target.host", Rejected(WithTarget(t => t with { Host = new string('h', 254) })).Field);
    }

    [Fact]
    public void ANullHostIsRejected() => Assert.Equal("target.host", Rejected(WithTarget(t => t with { Host = null! })).Field);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void APortOutsideTheValidRangeIsRejected(int port) => Assert.Equal("target.port", Rejected(WithTarget(t => t with { Port = port })).Field);

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(65535)]
    public void AnOmittedPortAndThePortBoundsAreAccepted(int? port) =>
        Assert.Equal(port, RestoreInputValidation.Validate(ArtifactId, WithTarget(t => t with { Port = port })).Target.Port);

    [Fact]
    public void IntegratedSecurityWithoutACredentialReferenceIsAccepted()
    {
        var target = RestoreInputValidation.Validate(ArtifactId, WithTarget(t => t with { IntegratedSecurity = true, DatabaseCredentialReference = null })).Target;
        Assert.True(target.IntegratedSecurity);
        Assert.Null(target.DatabaseCredentialReference);
    }

    [Fact]
    public void IntegratedSecurityTogetherWithACredentialReferenceIsRejected() =>
        Assert.Equal("target.databaseCredentialReference", Rejected(WithTarget(t => t with { IntegratedSecurity = true, DatabaseCredentialReference = "sql-cred" })).Field);

    [Fact]
    public void WithoutIntegratedSecurityACredentialReferenceIsRequired()
    {
        Assert.Equal("target.databaseCredentialReference", Rejected(WithTarget(t => t with { IntegratedSecurity = false, DatabaseCredentialReference = null })).Field);
        Assert.Equal("target.databaseCredentialReference", Rejected(WithTarget(t => t with { DatabaseCredentialReference = "" })).Field);
        Assert.Equal("target.databaseCredentialReference", Rejected(WithTarget(t => t with { DatabaseCredentialReference = "   " })).Field);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("dotted.name")]
    [InlineData("slash/name")]
    [InlineData("p@ss")]
    [InlineData("semi;colon")]
    [InlineData("tab\tname")]
    [InlineData("ünï")]
    public void ACredentialReferenceOutsideLettersDigitsDashAndUnderscoreIsRejected(string reference) =>
        Assert.Equal("target.databaseCredentialReference", Rejected(WithTarget(t => t with { DatabaseCredentialReference = reference })).Field);

    [Theory]
    [InlineData("a")]
    [InlineData("Sql-Prod_01")]
    public void ACredentialReferenceInsideTheCharsetIsAccepted(string reference) =>
        Assert.Equal(reference, RestoreInputValidation.Validate(ArtifactId, WithTarget(t => t with { DatabaseCredentialReference = reference })).Target.DatabaseCredentialReference);

    [Fact]
    public void ACredentialReferenceIsBoundedToSixtyFourCharacters()
    {
        Assert.Equal(64, RestoreInputValidation.Validate(ArtifactId, WithTarget(t => t with { DatabaseCredentialReference = new string('c', 64) })).Target.DatabaseCredentialReference!.Length);
        Assert.Equal("target.databaseCredentialReference", Rejected(WithTarget(t => t with { DatabaseCredentialReference = new string('c', 65) })).Field);
    }

    [Fact]
    public void ANullTargetIsRejected()
    {
        var failure = Rejected(Base with { Target = null! });
        Assert.Equal("target", failure.Field);
    }

    [Fact]
    public void ANullRequestIsAProgrammingError() =>
        Assert.Throws<ArgumentNullException>(() => RestoreInputValidation.Validate(ArtifactId, null!));

    // ---- database name and identifiers ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Sales")]
    [InlineData("Sales ")]
    [InlineData("Sa\tles")]
    public void InvalidDatabaseNameSyntaxIsRejected(string name)
    {
        var failure = Rejected(Base with { DatabaseName = name });
        Assert.Equal("databaseName", failure.Field);
        Assert.Equal("invalid_value", failure.Code);
    }

    [Fact]
    public void ANullOrOverlongDatabaseNameIsRejected()
    {
        Assert.Equal("databaseName", Rejected(Base with { DatabaseName = null! }).Field);
        Assert.Equal("databaseName", Rejected(Base with { DatabaseName = new string('d', 129) }).Field);
        Assert.Equal(128, RestoreInputValidation.Validate(ArtifactId, Base with { DatabaseName = new string('d', 128) }).DatabaseName.Length);
    }

    [Theory]
    [InlineData("Sales")]
    [InlineData("my db")]
    [InlineData("Ünïcødé数据")]
    public void OrdinaryDatabaseNamesAreAccepted(string name) =>
        Assert.Equal(name, RestoreInputValidation.Validate(ArtifactId, Base with { DatabaseName = name }).DatabaseName);

    [Fact]
    public void AnEmptyAgentIdIsRejected() => Assert.Equal("agentId", Rejected(Base with { AgentId = Guid.Empty }).Field);

    [Fact]
    public void AnEmptyReplicaIdIsRejectedWhileAnOmittedOneIsAccepted()
    {
        Assert.Equal("replicaId", Rejected(Base with { ReplicaId = Guid.Empty }).Field);
        RestoreInputValidation.Validate(ArtifactId, Base with { ReplicaId = null });
        RestoreInputValidation.Validate(ArtifactId, Base with { ReplicaId = ReplicaGuid });
    }

    [Fact]
    public void ARejectionNamesTheFieldButNeverEchoesTheValue()
    {
        var failure = Rejected(WithTarget(t => t with { Host = "evil host with secret-value" }));
        Assert.DoesNotContain("secret-value", failure.Message);
        Assert.DoesNotContain("evil", failure.Message);
        Assert.IsAssignableFrom<ArgumentException>(failure);
    }

    [Fact]
    public void TheFieldChecksRunInTheDocumentedOrder()
    {
        var everythingWrong = new StartRestoreRequest(Guid.Empty, new RestoreTargetRequest("bad host", 0, true, "x"), "", Guid.Empty, "Nope", "x");
        Assert.Equal("agentId", Rejected(everythingWrong).Field);
        everythingWrong = everythingWrong with { AgentId = AgentGuid };
        Assert.Equal("replicaId", Rejected(everythingWrong).Field);
        everythingWrong = everythingWrong with { ReplicaId = null };
        Assert.Equal("target.host", Rejected(everythingWrong).Field);
        everythingWrong = everythingWrong with { Target = everythingWrong.Target with { Host = "host" } };
        Assert.Equal("target.port", Rejected(everythingWrong).Field);
        everythingWrong = everythingWrong with { Target = everythingWrong.Target with { Port = 1 } };
        Assert.Equal("target.databaseCredentialReference", Rejected(everythingWrong).Field);
        everythingWrong = everythingWrong with { Target = everythingWrong.Target with { DatabaseCredentialReference = null } };
        Assert.Equal("databaseName", Rejected(everythingWrong).Field);
        everythingWrong = everythingWrong with { DatabaseName = "Sales" };
        Assert.Equal("mode", Rejected(everythingWrong).Field);
    }

    // ---- fingerprint ----



    [Fact]
    public void OmittedAndExplicitDefaultsShareOneFingerprint()
    {
        var baseline = Fingerprint(Base);

        Assert.Equal(baseline, Fingerprint(Base with { Mode = "NewDatabase" }));
        Assert.Equal(baseline, Fingerprint(Base with { Mode = "newdatabase" }));
        Assert.Equal(baseline, Fingerprint(Base with { Mode = "" }));
        Assert.Equal(baseline, Fingerprint(Base with { Options = new RestoreOptionsRequest() }));
        Assert.Equal(baseline, Fingerprint(Base with { Options = new RestoreOptionsRequest(false, true, true) }));
        Assert.Equal(baseline, Fingerprint(Base with { Options = new RestoreOptionsRequest(TerminateActiveConnections: false) }));
        Assert.Equal(baseline, Fingerprint(WithTarget(t => t with { Encrypt = true, TrustServerCertificate = false })));
    }

    [Fact]
    public void AnyRealDifferenceChangesTheFingerprint()
    {
        var variants = new Dictionary<string, string>
        {
            ["baseline"] = Fingerprint(Base),
            ["artifact"] = Fingerprint(Base, Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000ff")),
            ["agent"] = Fingerprint(Base with { AgentId = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000ff") }),
            ["replica"] = Fingerprint(Base with { ReplicaId = ReplicaGuid }),
            ["otherReplica"] = Fingerprint(Base with { ReplicaId = Guid.Parse("cccccccc-0000-0000-0000-0000000000ff") }),
            ["host"] = Fingerprint(WithTarget(t => t with { Host = "sql02.corp.local" })),
            ["port"] = Fingerprint(WithTarget(t => t with { Port = 1434 })),
            ["noPort"] = Fingerprint(WithTarget(t => t with { Port = null })),
            ["integrated"] = Fingerprint(WithTarget(t => t with { IntegratedSecurity = true, DatabaseCredentialReference = null })),
            ["credential"] = Fingerprint(WithTarget(t => t with { DatabaseCredentialReference = "other-cred" })),
            ["encrypt"] = Fingerprint(WithTarget(t => t with { Encrypt = false })),
            ["trust"] = Fingerprint(WithTarget(t => t with { TrustServerCertificate = true })),
            ["database"] = Fingerprint(Base with { DatabaseName = "Sales_Other" }),
            ["mode"] = Fingerprint(Base with { Mode = "ReplaceExistingDatabase", ConfirmOverwrite = "Sales_Restored" }),
            ["terminate"] = Fingerprint(Base with { Options = new RestoreOptionsRequest(TerminateActiveConnections: true) }),
            ["checksum"] = Fingerprint(Base with { Options = new RestoreOptionsRequest(VerifyChecksum: false) }),
            ["backup"] = Fingerprint(Base with { Options = new RestoreOptionsRequest(VerifyBackup: false) })
        };

        Assert.Equal(variants.Count, variants.Values.Distinct().Count());
    }

    [Fact]
    public void TheFingerprintIsCaseSensitiveInTheDatabaseName() =>
        Assert.NotEqual(Fingerprint(Base with { DatabaseName = "sales" }), Fingerprint(Base with { DatabaseName = "Sales" }));

    // ---- agent reports: start and progress ----

    private static BackupTaskStartRequest Start(Guid? token = null, string software = "1.0", string protocol = "1.0", string platform = "Linux") =>
        new() { LeaseToken = token ?? Token, SoftwareVersion = software, ProtocolVersion = protocol, Platform = platform };

    [Fact]
    public void AWellFormedStartIsValid() => Assert.True(RestoreInputValidation.IsValidStart(Start()));

    [Fact]
    public void AStartWithAnEmptyTokenIsInvalid() => Assert.False(RestoreInputValidation.IsValidStart(Start(Guid.Empty)));

    [Theory]
    [InlineData("", "1", "Linux")]
    [InlineData("1", "", "Linux")]
    [InlineData("1", "1", "")]
    [InlineData("1\n", "1", "Linux")]
    [InlineData("1", "1\0", "Linux")]
    [InlineData("1", "1", "Li\tnux")]
    public void AStartWithBlankOrControlCharacterMetadataIsInvalid(string software, string protocol, string platform) =>
        Assert.False(RestoreInputValidation.IsValidStart(Start(software: software, protocol: protocol, platform: platform)));

    [Fact]
    public void StartMetadataIsBoundedTo64And32And32Characters()
    {
        Assert.True(RestoreInputValidation.IsValidStart(Start(software: new string('s', 64), protocol: new string('p', 32), platform: new string('l', 32))));
        Assert.False(RestoreInputValidation.IsValidStart(Start(software: new string('s', 65))));
        Assert.False(RestoreInputValidation.IsValidStart(Start(protocol: new string('p', 33))));
        Assert.False(RestoreInputValidation.IsValidStart(Start(platform: new string('l', 33))));
        Assert.False(RestoreInputValidation.IsValidStart(Start(software: null!)));
    }

    private static BackupTaskProgressRequest Progress(string stage, double? percent = null, Guid? token = null) => new() { LeaseToken = token ?? Token, Stage = stage, Percent = percent };

    [Theory]
    [InlineData(RestoreStages.Preparing)]
    [InlineData(RestoreStages.DownloadingArtifact)]
    [InlineData(RestoreStages.VerifyingChecksum)]
    [InlineData(RestoreStages.InspectingBackup)]
    [InlineData(RestoreStages.PreparingTarget)]
    [InlineData(RestoreStages.Restoring)]
    [InlineData(RestoreStages.Finalizing)]
    public void EveryKnownNonTerminalStageIsValidProgress(string stage) => Assert.True(RestoreInputValidation.IsValidProgress(Progress(stage, 10)));

    [Theory]
    [InlineData(RestoreStages.Completed)]
    [InlineData(RestoreStages.Failed)]
    [InlineData(RestoreStages.Cancelled)]
    [InlineData("")]
    [InlineData("Nope")]
    [InlineData("restoring")]
    public void TerminalAndUnknownStagesAreNotProgress(string stage) => Assert.False(RestoreInputValidation.IsValidProgress(Progress(stage, 10)));

    [Fact]
    public void ProgressWithAnEmptyTokenIsInvalid() => Assert.False(RestoreInputValidation.IsValidProgress(Progress(RestoreStages.Restoring, 10, Guid.Empty)));

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(50.5)]
    [InlineData(100.0)]
    public void ProgressPercentagesInsideTheRangeAreValid(double? percent) => Assert.True(RestoreInputValidation.IsValidProgress(Progress(RestoreStages.Restoring, percent)));

    [Theory]
    [InlineData(-0.001)]
    [InlineData(100.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ProgressPercentagesOutsideTheRangeAreInvalid(double percent) => Assert.False(RestoreInputValidation.IsValidProgress(Progress(RestoreStages.Restoring, percent)));

    // ---- agent reports: completion ----

    private static RestoreTaskCompletionRequest Completion(string status, string? code, string targetState, RestoreTaskResult? result = null, Guid? token = null) =>
        new() { LeaseToken = token ?? Token, Status = status, FailureCode = code, TargetState = targetState, Result = result };

    public static TheoryData<string> ReportableCodes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var code in RestoreFailureCodes.AgentReportable) data.Add(code);
            return data;
        }
    }

    [Fact]
    public void ASuccessIsAnOnlineTargetAndNoCode()
    {
        var completion = RestoreInputValidation.ToCompletion(Completion("Succeeded", null, "Online"));
        Assert.Equal(new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, null), completion);
    }

    [Theory]
    [InlineData("NotModified")]
    [InlineData("PartiallyRestored")]
    [InlineData("Unknown")]
    public void ASuccessWithAnythingButAnOnlineTargetIsRejected(string state) =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Succeeded", null, state)));

    [Fact]
    public void ASuccessWithAFailureCodeIsRejected() =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Succeeded", RestoreFailureCodes.InternalError, "Online")));

    [Theory]
    [MemberData(nameof(ReportableCodes))]
    public void EveryReportableCodeMapsForEveryNonOnlineTargetState(string code)
    {
        foreach (var (text, state) in new[] { ("NotModified", RestoreTargetState.NotModified), ("PartiallyRestored", RestoreTargetState.PartiallyRestored), ("Unknown", RestoreTargetState.Unknown) })
        {
            var completion = RestoreInputValidation.ToCompletion(Completion("Failed", code, text));
            Assert.Equal(new RestoreCompletion(RestoreStatus.Failed, code, state, null), completion);
        }
    }

    [Theory]
    [MemberData(nameof(ReportableCodes))]
    public void AFailureNeverLeavesAnOnlineTarget(string code) =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Failed", code, "Online")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cancelled")]
    [InlineData("AgentLost")]
    [InlineData("NotClaimed")]
    [InlineData("bogus")]
    [InlineData("sqlrestorefailed")]
    public void AFailureNeedsAnAgentReportableCode(string? code) =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Failed", code, "NotModified")));

    [Theory]
    [InlineData(null)]
    [InlineData("Cancelled")]
    public void ACancellationIsNormalisedToTheCancelledCodeWhicheverWayItWasSent(string? code)
    {
        var completion = RestoreInputValidation.ToCompletion(Completion("Cancelled", code, "PartiallyRestored"));
        Assert.Equal(new RestoreCompletion(RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.PartiallyRestored, null), completion);
    }

    [Theory]
    [InlineData("NotModified", RestoreTargetState.NotModified)]
    [InlineData("PartiallyRestored", RestoreTargetState.PartiallyRestored)]
    [InlineData("Unknown", RestoreTargetState.Unknown)]
    public void ACancellationKeepsTheReportedNonOnlineTargetState(string text, RestoreTargetState expected) =>
        Assert.Equal(expected, RestoreInputValidation.ToCompletion(Completion("Cancelled", null, text)).TargetState);

    [Fact]
    public void ACancellationNeverLeavesAnOnlineTarget() =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Cancelled", null, "Online")));

    [Theory]
    [InlineData(RestoreFailureCodes.SqlRestoreFailed)]
    [InlineData(RestoreFailureCodes.AgentLost)]
    [InlineData(RestoreFailureCodes.NotClaimed)]
    [InlineData("")]
    [InlineData("cancelled")]
    public void ACancellationWithAnyOtherCodeIsRejected(string code) =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Cancelled", code, "NotModified")));

    [Theory]
    [InlineData("")]
    [InlineData("Pending")]
    [InlineData("Leased")]
    [InlineData("Running")]
    [InlineData("AgentLost")]
    [InlineData("succeeded")]
    [InlineData("SUCCEEDED")]
    [InlineData("4")]
    [InlineData(" Succeeded")]
    public void OnlyTheThreeReportedOutcomesAreAcceptedAsStatus(string status) =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion(status, null, "Online")));

    [Fact]
    public void ANullStatusIsRejected() => Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion(null!, null, "Online")));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("online")]
    [InlineData("ONLINE")]
    [InlineData("Restoring")]
    [InlineData(" Online")]
    public void TheTargetStateMustBeOneOfTheFourNames(string state)
    {
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Succeeded", null, state)));
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Failed", RestoreFailureCodes.InternalError, state)));
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Cancelled", null, state)));
    }

    [Fact]
    public void ANullTargetStateIsRejected() => Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Succeeded", null, null!)));

    [Fact]
    public void ACompletionWithAnEmptyLeaseTokenIsRejected() =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Succeeded", null, "Online", token: Guid.Empty)));

    [Fact]
    public void ANullCompletionIsAProgrammingError() => Assert.Throws<ArgumentNullException>(() => RestoreInputValidation.ToCompletion(null!));

    // ---- agent reports: result ----

    private static RestoreTaskResult ValidResult() => new()
    {
        PreCheck =
        [
            new RestorePreCheckItemResult("target-reachable", "Passed", "ok", "The target server is reachable."),
            new RestorePreCheckItemResult("disk-space", "Warning", "low_space.margin-1", "Free space is close to the limit.")
        ],
        Warnings = [new RestoreWarningResult("W-001", "The database was renamed.")],
        SourceBackup = new RestoreSourceBackupSummary
        {
            DatabaseName = "Sales", BackupType = "Full", SourceServerName = "SRC01", SourceServerVersion = "16.0.1000", BackupStartedAt = At.DateTime.AddHours(-2), BackupFinishedAt = At.DateTime.AddHours(-1),
            Position = 1, BackupSizeBytes = 123456789, CompatibilityLevel = 160, Collation = "SQL_Latin1_General_CP1_CI_AS", IsCopyOnly = true, HasChecksums = true, IsCompressed = true
        },
        TargetServer = new RestoreTargetServerSummary { ServerName = "DST01", InstanceName = "MSSQLSERVER", Version = "16.0.4100", Edition = "Developer", HostPlatform = "Linux" },
        FileMappings =
        [
            new RestoreFileMappingResult { LogicalName = "Sales", FileType = "ROWS", FileGroupName = "PRIMARY", SourcePhysicalName = "/src/Sales.mdf", TargetPhysicalName = "/var/opt/mssql/data/Sales_Restored.mdf", SizeBytes = 1000 },
            new RestoreFileMappingResult { LogicalName = "Sales_log", FileType = "LOG", TargetPhysicalName = "/var/opt/mssql/data/Sales_Restored_log.ldf", SizeBytes = 500 }
        ],
        StartedAt = At, FinishedAt = At.AddMinutes(10), RestoreStartedAt = At.AddMinutes(2), RestoreFinishedAt = At.AddMinutes(9),
        ChecksumVerified = true, BackupVerified = true, DatabaseState = "ONLINE", TargetStateNote = "The restored database is online."
    };

    private static string ResultJsonOf(RestoreTaskResult result) =>
        RestoreInputValidation.ToCompletion(Completion("Succeeded", null, "Online", result)).ResultJson!;

    private static void AssertRejectedResult(RestoreTaskResult result) =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Succeeded", null, "Online", result)));

    [Fact]
    public void AValidResultRoundTripsWithEveryField()
    {
        var expected = ValidResult();
        var json = ResultJsonOf(expected);
        var actual = RestoreInputValidation.ReadResult(json)!;

        Assert.NotNull(actual);
        Assert.Equal(expected.PreCheck, actual.PreCheck);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.SourceBackup, actual.SourceBackup);
        Assert.Equal(expected.TargetServer, actual.TargetServer);
        Assert.Equal(expected.FileMappings, actual.FileMappings);
        Assert.Equal(expected.StartedAt, actual.StartedAt);
        Assert.Equal(expected.FinishedAt, actual.FinishedAt);
        Assert.Equal(expected.RestoreStartedAt, actual.RestoreStartedAt);
        Assert.Equal(expected.RestoreFinishedAt, actual.RestoreFinishedAt);
        Assert.True(actual.ChecksumVerified);
        Assert.True(actual.BackupVerified);
        Assert.Equal("ONLINE", actual.DatabaseState);
        Assert.Equal("The restored database is online.", actual.TargetStateNote);
    }

    [Fact]
    public void TheStoredResultUsesCamelCaseNamesAndIsWithinTheDomainLimit()
    {
        var json = ResultJsonOf(ValidResult());
        Assert.Contains("\"preCheck\"", json);
        Assert.Contains("\"fileMappings\"", json);
        Assert.True(json.Length <= RestoreOperation.MaxResultJsonLength);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void AnEmptyResultIsAccepted()
    {
        var json = ResultJsonOf(new RestoreTaskResult());
        var read = RestoreInputValidation.ReadResult(json)!;
        Assert.Empty(read.PreCheck);
        Assert.Empty(read.Warnings);
        Assert.Empty(read.FileMappings);
        Assert.Null(read.SourceBackup);
        Assert.False(read.ChecksumVerified);
    }

    [Fact]
    public void AnAbsentResultStaysAbsent() => Assert.Null(RestoreInputValidation.ToCompletion(Completion("Succeeded", null, "Online")).ResultJson);

    [Fact]
    public void AFailedRestoreMayCarryAResultToo()
    {
        var completion = RestoreInputValidation.ToCompletion(Completion("Failed", RestoreFailureCodes.PreCheckFailed, "NotModified", ValidResult()));
        Assert.NotNull(RestoreInputValidation.ReadResult(completion.ResultJson));
    }

    [Fact]
    public void ResultListsAreBoundedAndMustBePresent()
    {
        var item = new RestorePreCheckItemResult("c", "Passed", "ok", "m");
        var warning = new RestoreWarningResult("w", "m");
        var mapping = new RestoreFileMappingResult { LogicalName = "l", TargetPhysicalName = "/t" };

        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { PreCheck = [.. Enumerable.Repeat(item, RestoreInputValidation.MaxPreCheckItems)] }));
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { Warnings = [.. Enumerable.Repeat(warning, RestoreInputValidation.MaxWarnings)] }));
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { FileMappings = [.. Enumerable.Repeat(mapping, RestoreInputValidation.MaxFileMappings)] }));

        AssertRejectedResult(new RestoreTaskResult { PreCheck = [.. Enumerable.Repeat(item, RestoreInputValidation.MaxPreCheckItems + 1)] });
        AssertRejectedResult(new RestoreTaskResult { Warnings = [.. Enumerable.Repeat(warning, RestoreInputValidation.MaxWarnings + 1)] });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = [.. Enumerable.Repeat(mapping, RestoreInputValidation.MaxFileMappings + 1)] });
        AssertRejectedResult(new RestoreTaskResult { PreCheck = null! });
        AssertRejectedResult(new RestoreTaskResult { Warnings = null! });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = null! });
    }

    [Fact]
    public void ListItemsMayNotBeNull()
    {
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [null!] });
        AssertRejectedResult(new RestoreTaskResult { Warnings = [null!] });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = [null!] });
    }

    [Theory]
    [InlineData("Passed")]
    [InlineData("Warning")]
    [InlineData("Failed")]
    [InlineData("Skipped")]
    public void EveryPreCheckStatusIsAccepted(string status) =>
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", status, "ok", "m")] }));

    [Theory]
    [InlineData("passed")]
    [InlineData("Bogus")]
    [InlineData("")]
    [InlineData("PASSED")]
    public void AnInvalidPreCheckStatusIsRejected(string status) =>
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", status, "ok", "m")] });

    [Theory]
    [InlineData("")]
    [InlineData("bad code")]
    [InlineData("bad/code")]
    [InlineData("bad:code")]
    [InlineData("bad\ncode")]
    [InlineData("codé")]
    public void APreCheckReasonCodeOutsideTheCharsetIsRejected(string reason) =>
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", "Passed", reason, "m")] });

    [Theory]
    [InlineData("a")]
    [InlineData("Low_Space-1.x")]
    public void APreCheckReasonCodeInsideTheCharsetIsAccepted(string reason) =>
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", "Passed", reason, "m")] }));

    [Fact]
    public void PreCheckFieldLengthsAreBounded()
    {
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult(new string('i', 64), "Passed", new string('r', 64), new string('m', 512))] }));
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult(new string('i', 65), "Passed", "ok", "m")] });
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", "Passed", new string('r', 65), "m")] });
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", "Passed", "ok", new string('m', 513))] });
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("", "Passed", "ok", "m")] });
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult(null!, "Passed", "ok", "m")] });
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", "Passed", "ok", null!)] }));
    }

    [Fact]
    public void WarningFieldsAreBounded()
    {
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { Warnings = [new RestoreWarningResult(new string('w', 64), new string('m', 512))] }));
        AssertRejectedResult(new RestoreTaskResult { Warnings = [new RestoreWarningResult(new string('w', 65), "m")] });
        AssertRejectedResult(new RestoreTaskResult { Warnings = [new RestoreWarningResult("", "m")] });
        AssertRejectedResult(new RestoreTaskResult { Warnings = [new RestoreWarningResult("w", new string('m', 513))] });
    }

    [Fact]
    public void FileMappingFieldsAreBoundedAndTheRequiredOnesPresent()
    {
        RestoreTaskResult With(RestoreFileMappingResult mapping) => new() { FileMappings = [mapping] };
        var valid = new RestoreFileMappingResult { LogicalName = "l", TargetPhysicalName = "/t" };

        Assert.NotNull(ResultJsonOf(With(valid with { LogicalName = new string('l', 128), FileType = new string('t', 16), FileGroupName = new string('g', 128),
            SourcePhysicalName = new string('s', 1024), TargetPhysicalName = new string('p', 1024), SizeBytes = 0 })));
        AssertRejectedResult(With(valid with { LogicalName = new string('l', 129) }));
        AssertRejectedResult(With(valid with { LogicalName = "" }));
        AssertRejectedResult(With(valid with { FileType = new string('t', 17) }));
        AssertRejectedResult(With(valid with { FileGroupName = new string('g', 129) }));
        AssertRejectedResult(With(valid with { SourcePhysicalName = new string('s', 1025) }));
        AssertRejectedResult(With(valid with { TargetPhysicalName = new string('p', 1025) }));
        AssertRejectedResult(With(valid with { TargetPhysicalName = "" }));
        AssertRejectedResult(With(valid with { SizeBytes = -1 }));
    }

    [Fact]
    public void SourceBackupSummaryFieldsAreBoundedAndNonNegative()
    {
        RestoreTaskResult With(RestoreSourceBackupSummary summary) => new() { SourceBackup = summary };
        var valid = new RestoreSourceBackupSummary { DatabaseName = "Sales", BackupType = "Full" };

        Assert.NotNull(ResultJsonOf(With(valid with { DatabaseName = new string('d', 128), BackupType = new string('t', 32), SourceServerName = new string('n', 256),
            SourceServerVersion = new string('v', 64), Collation = new string('c', 128), CompatibilityLevel = 1000, Position = 0, BackupSizeBytes = 0 })));
        AssertRejectedResult(With(valid with { DatabaseName = new string('d', 129) }));
        AssertRejectedResult(With(valid with { BackupType = new string('t', 33) }));
        AssertRejectedResult(With(valid with { SourceServerName = new string('n', 257) }));
        AssertRejectedResult(With(valid with { SourceServerVersion = new string('v', 65) }));
        AssertRejectedResult(With(valid with { Collation = new string('c', 129) }));
        AssertRejectedResult(With(valid with { Position = -1 }));
        AssertRejectedResult(With(valid with { BackupSizeBytes = -1 }));
        AssertRejectedResult(With(valid with { CompatibilityLevel = -1 }));
        AssertRejectedResult(With(valid with { CompatibilityLevel = 1001 }));
    }

    [Fact]
    public void TargetServerSummaryFieldsAreBounded()
    {
        RestoreTaskResult With(RestoreTargetServerSummary summary) => new() { TargetServer = summary };

        Assert.NotNull(ResultJsonOf(With(new RestoreTargetServerSummary { ServerName = new string('n', 256), InstanceName = new string('i', 256), Version = new string('v', 64),
            Edition = new string('e', 128), HostPlatform = new string('p', 64) })));
        AssertRejectedResult(With(new RestoreTargetServerSummary { ServerName = new string('n', 257) }));
        AssertRejectedResult(With(new RestoreTargetServerSummary { InstanceName = new string('i', 257) }));
        AssertRejectedResult(With(new RestoreTargetServerSummary { Version = new string('v', 65) }));
        AssertRejectedResult(With(new RestoreTargetServerSummary { Edition = new string('e', 129) }));
        AssertRejectedResult(With(new RestoreTargetServerSummary { HostPlatform = new string('p', 65) }));
    }

    [Fact]
    public void DatabaseStateAndTheTargetStateNoteAreBounded()
    {
        Assert.NotNull(ResultJsonOf(new RestoreTaskResult { DatabaseState = new string('d', 64), TargetStateNote = new string('n', 512) }));
        AssertRejectedResult(new RestoreTaskResult { DatabaseState = new string('d', 65) });
        AssertRejectedResult(new RestoreTaskResult { TargetStateNote = new string('n', 513) });
    }

    private static readonly string[] UnsafeStrings =
    [
        "nul\0char", "line\nbreak", "carriage\rreturn", "tab\there", "bell\a", "escape\u001b[0m", "next\u0085line", "lone\ud800high", "lone\udc00low", "trailing\ud83d", "\ude00leading"
    ];

    [Fact]
    public void ControlCharactersAndLoneSurrogatesAreRejectedInEveryStringOfTheResult()
    {
        foreach (var unsafeText in UnsafeStrings) AssertEveryStringRejects(unsafeText);
    }

    private static void AssertEveryStringRejects(string unsafeText)
    {
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c" + unsafeText, "Passed", "ok", "m")] });
        AssertRejectedResult(new RestoreTaskResult { PreCheck = [new RestorePreCheckItemResult("c", "Passed", "ok", unsafeText)] });
        AssertRejectedResult(new RestoreTaskResult { Warnings = [new RestoreWarningResult(unsafeText, "m")] });
        AssertRejectedResult(new RestoreTaskResult { Warnings = [new RestoreWarningResult("w", unsafeText)] });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = [new RestoreFileMappingResult { LogicalName = unsafeText, TargetPhysicalName = "/t" }] });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = [new RestoreFileMappingResult { LogicalName = "l", TargetPhysicalName = unsafeText }] });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = [new RestoreFileMappingResult { LogicalName = "l", FileType = unsafeText, TargetPhysicalName = "/t" }] });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = [new RestoreFileMappingResult { LogicalName = "l", FileGroupName = unsafeText, TargetPhysicalName = "/t" }] });
        AssertRejectedResult(new RestoreTaskResult { FileMappings = [new RestoreFileMappingResult { LogicalName = "l", SourcePhysicalName = unsafeText, TargetPhysicalName = "/t" }] });
        AssertRejectedResult(new RestoreTaskResult { SourceBackup = new RestoreSourceBackupSummary { DatabaseName = unsafeText } });
        AssertRejectedResult(new RestoreTaskResult { SourceBackup = new RestoreSourceBackupSummary { Collation = unsafeText } });
        AssertRejectedResult(new RestoreTaskResult { TargetServer = new RestoreTargetServerSummary { ServerName = unsafeText } });
        AssertRejectedResult(new RestoreTaskResult { TargetServer = new RestoreTargetServerSummary { Edition = unsafeText } });
        AssertRejectedResult(new RestoreTaskResult { DatabaseState = unsafeText });
        AssertRejectedResult(new RestoreTaskResult { TargetStateNote = unsafeText });
    }

    [Fact]
    public void WellFormedSurrogatePairsAndOtherUnicodeAreAccepted()
    {
        var result = new RestoreTaskResult { TargetStateNote = "Restored 😀 — données 数据", Warnings = [new RestoreWarningResult("w", "naïve café 😀")] };
        var read = RestoreInputValidation.ReadResult(ResultJsonOf(result))!;
        Assert.Equal("Restored 😀 — données 数据", read.TargetStateNote);
        Assert.Equal("naïve café 😀", read.Warnings[0].Message);
    }

    [Fact]
    public void AResultWhoseSerializedFormExceedsTheDomainLimitIsRejectedEvenIfEveryFieldIsValid()
    {
        var mappings = Enumerable.Range(0, RestoreInputValidation.MaxFileMappings)
            .Select(index => new RestoreFileMappingResult { LogicalName = "file" + index, SourcePhysicalName = new string('s', 1024), TargetPhysicalName = new string('t', 1024), SizeBytes = 1 })
            .ToArray();
        var failure = Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Succeeded", null, "Online", new RestoreTaskResult { FileMappings = mappings })));
        Assert.Contains("too large", failure.Message);
    }

    [Fact]
    public void AnInvalidResultIsRejectedBeforeAnythingIsStored() =>
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Completion("Failed", RestoreFailureCodes.InternalError, "Unknown", new RestoreTaskResult { DatabaseState = "bad\0state" })));

    // ---- ReadResult ----

    [Fact]
    public void ReadingNoResultYieldsNull() => Assert.Null(RestoreInputValidation.ReadResult(null));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{\"checksumVerified\":\"yes\"}")]
    [InlineData("{\"preCheck\":42}")]
    public void UnreadableStoredTextYieldsNull(string json) => Assert.Null(RestoreInputValidation.ReadResult(json));

    [Fact]
    public void AnEmptyJsonObjectReadsAsAnEmptyResult()
    {
        var result = RestoreInputValidation.ReadResult("{}");
        Assert.NotNull(result);
        Assert.Empty(result.PreCheck);
        Assert.False(result.BackupVerified);
    }

    [Fact]
    public void ReadingIgnoresPropertyNameCase()
    {
        var result = RestoreInputValidation.ReadResult("{\"DatabaseState\":\"ONLINE\",\"checksumverified\":true}")!;
        Assert.Equal("ONLINE", result.DatabaseState);
        Assert.True(result.ChecksumVerified);
    }
}
