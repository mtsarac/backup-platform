using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class StorageConnectionServiceTestFlowTests
{
    private const string LeakyText = "sftp.internal.corp password=hunter2 /var/secret/path";

    private static StorageTestResult Ok { get; } = new(StorageTestStatus.Succeeded, StorageTestCodes.Ok, "fine");

    private static ScriptedTester Tester(string providerType, Func<StorageConnection, ISecretProvider, Task<StorageTestResult>> test) => new(providerType, test);

    private static ScriptedTester Returning(string providerType, StorageTestResult result) => Tester(providerType, (_, _) => Task.FromResult(result));

    private static ScriptedTester Throwing(string providerType, Exception exception) => Tester(providerType, (_, _) => throw exception);

    private static StorageConnection FakeConnection(ServiceHarness h, string name = "Archive") =>
        h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, name, configuration: new Dictionary<string, string> { ["host"] = "h" });

    private Task<StorageResult<StorageTestOutcome>> RunTest(ServiceHarness h, Guid id, StorageScope? scope = null) =>
        h.Service.TestAsync(scope ?? h.TenantScope(), id, h.Actor, CancellationToken.None);

    [Fact]
    public async Task ASuccessfulTesterResultIsPersistedAuditedAndReturned()
    {
        var h = new ServiceHarness(Returning(TestProviders.Fake, Ok));
        var connection = FakeConnection(h);
        h.Clock.Advance(TimeSpan.FromMinutes(3));

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        var outcome = result.Value!;
        Assert.Equal(StorageTestStatus.Succeeded, outcome.Test.Status);
        Assert.Equal(StorageTestCodes.Ok, outcome.Test.Code);
        Assert.Equal(ServiceHarness.Start.AddMinutes(3), outcome.Test.TestedAt);
        Assert.True(outcome.DurationMilliseconds >= 0);
        Assert.Equal(outcome.Test, outcome.Connection.LastTest);
        Assert.Equal(StorageTestStatus.Succeeded, connection.LastTestStatus);
        Assert.Equal(StorageTestCodes.Ok, connection.LastTestCode);
        var audit = Assert.Single(h.Store.Audit);
        Assert.Equal(StorageConnectionAuditActions.Tested, audit.Action);
        Assert.Equal("Succeeded", audit.Details["status"]);
        Assert.Equal(StorageTestCodes.Ok, audit.Details["code"]);
    }

    [Fact]
    public async Task TheSummaryMessageIsTheFixedSentenceForTheCodeNotWhateverTheTesterSaid()
    {
        var h = new ServiceHarness(Returning(TestProviders.Fake, new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.AccessDenied, LeakyText)));
        var connection = FakeConnection(h);

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageTestMessages.For(StorageTestCodes.AccessDenied), result.Value!.Test.Message);
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentSideTesterNeverReportsSuccess()
    {
        var h = new ServiceHarness(new AgentSideConnectionTester(TestProviders.Plain));
        var connection = h.Seed(TestProviders.Plain, StorageOwnership.TenantManaged, "Plain");

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageTestStatus.NotTestable, result.Value!.Test.Status);
        Assert.Equal(StorageTestCodes.AgentSideStorage, result.Value.Test.Code);
        Assert.NotEqual(StorageTestStatus.Succeeded, connection.LastTestStatus);
        Assert.Equal(StorageTestStatus.NotTestable, connection.LastTestStatus);
        Assert.Equal("NotTestable", Assert.Single(h.Store.Audit).Details["status"]);
    }

    [Fact]
    public async Task AnAvailableProviderWithoutARegisteredTesterFailsAsNotImplementedNeverAsPassing()
    {
        var h = new ServiceHarness();
        var connection = FakeConnection(h);

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageTestStatus.Failed, result.Value!.Test.Status);
        Assert.Equal(StorageTestCodes.ProviderNotImplemented, result.Value.Test.Code);
        Assert.Equal(StorageTestStatus.Failed, connection.LastTestStatus);
    }

    [Theory]
    [InlineData(TestProviders.Planned)]
    [InlineData("unknown-provider")]
    public async Task APlannedOrUnknownProviderFailsWithProviderNotImplementedEvenWhenATesterIsRegisteredForIt(string providerType)
    {
        var tester = Returning(providerType, Ok);
        var h = new ServiceHarness(tester);
        var connection = h.Seed(providerType, StorageOwnership.TenantManaged, "Legacy");

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal(StorageTestStatus.Failed, result.Value!.Test.Status);
        Assert.Equal(StorageTestCodes.ProviderNotImplemented, result.Value.Test.Code);
        Assert.Equal(0, tester.Calls);
        Assert.Equal(StorageTestStatus.Failed, connection.LastTestStatus);
    }

    [Fact]
    public async Task AnUnexpectedTesterExceptionBecomesAStableFailureThatDoesNotRepeatTheExceptionText()
    {
        var h = new ServiceHarness(Throwing(TestProviders.Fake, new InvalidOperationException(LeakyText)));
        var connection = FakeConnection(h);

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal(StorageTestStatus.Failed, result.Value!.Test.Status);
        Assert.Equal(StorageTestCodes.Unexpected, result.Value.Test.Code);
        Assert.Equal(StorageTestMessages.For(StorageTestCodes.Unexpected), result.Value.Test.Message);
        var everything = JsonSerializer.Serialize(new { result.Value, Audit = h.Store.Audit.Select(item => item.Details), connection.LastTestCode });
        Assert.DoesNotContain("hunter2", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("sftp.internal", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/secret", everything, StringComparison.Ordinal);
        Assert.NotEmpty(h.Logger.Entries);
        Assert.DoesNotContain("hunter2", h.Logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sftp.internal", h.Logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/secret", h.Logger.AllText, StringComparison.Ordinal);
        Assert.Equal(StorageTestCodes.Unexpected, connection.LastTestCode);
    }

    [Fact]
    public async Task AnUnavailableSecretIsReportedAsSuchWithoutDetails()
    {
        var h = new ServiceHarness(Throwing(TestProviders.Fake, new SecretUnavailableException()));
        var connection = FakeConnection(h);

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageTestStatus.Failed, result.Value!.Test.Status);
        Assert.Equal(StorageTestCodes.SecretUnavailable, result.Value.Test.Code);
        Assert.Equal(StorageTestMessages.For(StorageTestCodes.SecretUnavailable), result.Value.Test.Message);
    }

    [Fact]
    public async Task CancellationIsNotSwallowedAndRecordsNothing()
    {
        var h = new ServiceHarness(Throwing(TestProviders.Fake, new OperationCanceledException()));
        var connection = FakeConnection(h);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunTest(h, connection.Id));

        Assert.Null(connection.LastTestStatus);
        Assert.Empty(h.Store.Audit);
    }

    [Fact]
    public async Task TheTesterIsHandedTheConnectionAndTheSecretStoreSoItCanResolveCredentials()
    {
        string? revealed = null;
        var tester = Tester(TestProviders.Fake, async (connection, secrets) =>
        {
            revealed = (await secrets.ResolveAsync(new SecretReference(connection.SecretReference!), CancellationToken.None)).Reveal();
            return Ok;
        });
        var h = new ServiceHarness(tester);
        var connection = await h.CreateFakeAsync("Archive", "resolved-by-tester");

        await RunTest(h, connection.Id);

        Assert.Contains("resolved-by-tester", revealed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewTestReplacesTheLastResult()
    {
        var outcomes = new Queue<StorageTestResult>(new[] { new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.IoError, "x"), Ok });
        var h = new ServiceHarness(Tester(TestProviders.Fake, (_, _) => Task.FromResult(outcomes.Dequeue())));
        var connection = FakeConnection(h);

        await RunTest(h, connection.Id);
        var second = await RunTest(h, connection.Id);

        Assert.Equal(StorageTestStatus.Succeeded, second.Value!.Connection.LastTest!.Status);
        Assert.Equal(2, h.Store.Audit.Count);
    }

    // ---- who may test ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlatformCannotTestATenantManagedConnectionAndTheTesterIsNotRun()
    {
        var tester = Returning(TestProviders.Fake, Ok);
        var h = new ServiceHarness(tester);
        var connection = FakeConnection(h);

        var result = await RunTest(h, connection.Id, StorageScope.Platform);

        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.TenantManaged), (result.Outcome, result.Code));
        Assert.Equal(0, tester.Calls);
        Assert.Null(connection.LastTestStatus);
    }

    [Fact]
    public async Task TenantCannotTestAPlatformManagedConnectionButPlatformCan()
    {
        var tester = Returning(TestProviders.PlatformOnly, Ok);
        var h = new ServiceHarness(tester);
        var connection = h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        var tenant = await RunTest(h, connection.Id);
        var platform = await RunTest(h, connection.Id, StorageScope.Platform);

        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.PlatformManaged), (tenant.Outcome, tenant.Code));
        Assert.Equal(StorageConnectionOutcome.Changed, platform.Outcome);
        Assert.Equal(1, tester.Calls);
    }

    [Fact]
    public async Task ViewerScopeCannotTest()
    {
        var tester = Returning(TestProviders.Fake, Ok);
        var h = new ServiceHarness(tester);
        var connection = FakeConnection(h);

        var result = await RunTest(h, connection.Id, h.TenantScope(canManage: false));

        Assert.Equal(StorageConnectionOutcome.Rejected, result.Outcome);
        Assert.Equal(0, tester.Calls);
    }

    [Fact]
    public async Task TestingAnotherTenantsConnectionIsNotFoundAndTheTesterIsNotRun()
    {
        var tester = Returning(TestProviders.Fake, Ok);
        var h = new ServiceHarness(tester);
        var foreign = h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Foreign", h.OtherTenant);

        var result = await RunTest(h, foreign.Id);

        Assert.Equal(StorageConnectionOutcome.NotFound, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(0, tester.Calls);
        Assert.Null(foreign.LastTestStatus);
    }

    [Fact]
    public async Task IfTheConnectionDisappearsWhileTestingTheOutcomeIsReportedWithoutAView()
    {
        var h = new ServiceHarness(Returning(TestProviders.Fake, Ok));
        var connection = FakeConnection(h);
        h.Store.MutateOutcomeOverride = StorageConnectionOutcome.NotFound;

        var result = await RunTest(h, connection.Id);

        Assert.Equal(StorageConnectionOutcome.NotFound, result.Outcome);
        Assert.Null(result.Value);
    }

    // ---- last test in views ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AViewShowsTheLastTestWithTheFixedMessageForItsCode()
    {
        var h = new ServiceHarness();
        var connection = FakeConnection(h);
        connection.RecordTest(StorageTestStatus.Failed, StorageTestCodes.InsufficientSpace, ServiceHarness.Start);

        var view = (await h.Service.GetAsync(h.TenantScope(), connection.Id, CancellationToken.None))!;

        Assert.Equal(new StorageTestSummary(StorageTestStatus.Failed, StorageTestCodes.InsufficientSpace, StorageTestMessages.For(StorageTestCodes.InsufficientSpace), ServiceHarness.Start), view.LastTest);
    }

    [Fact]
    public async Task AnUntestedConnectionHasNoLastTest()
    {
        var h = new ServiceHarness();
        var connection = FakeConnection(h);

        Assert.Null((await h.Service.GetAsync(h.TenantScope(), connection.Id, CancellationToken.None))!.LastTest);
    }

}
