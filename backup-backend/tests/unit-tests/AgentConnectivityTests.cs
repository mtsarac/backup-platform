using BackupPlatform.Agents;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Protocol;
using BackupPlatform.Domain.Agents;

namespace BackupPlatform.UnitTests;

public sealed class AgentConnectivityEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    public void NeverSeenIsUnknownNotOffline() =>
        Assert.Equal(AgentConnectivityState.Unknown, AgentConnectivityEvaluator.Evaluate(null, Now, Timeout));

    [Theory]
    [InlineData(0, AgentConnectivityState.Online)]
    [InlineData(119, AgentConnectivityState.Online)]
    [InlineData(120, AgentConnectivityState.Offline)]
    [InlineData(3600, AgentConnectivityState.Offline)]
    public void StateFollowsTheAgeOfTheLastSignOfLifeAgainstTheTimeout(int ageSeconds, AgentConnectivityState expected) =>
        Assert.Equal(expected, AgentConnectivityEvaluator.Evaluate(Now.AddSeconds(-ageSeconds), Now, Timeout));

    [Fact]
    public void ALastSeenSlightlyInTheFutureIsStillOnline() =>
        Assert.Equal(AgentConnectivityState.Online, AgentConnectivityEvaluator.Evaluate(Now.AddSeconds(5), Now, Timeout));

    [Theory]
    [InlineData(AgentStatus.Revoked, AgentConnectivityState.Online, true, AgentOperationalStatus.Revoked)]
    [InlineData(AgentStatus.Disabled, AgentConnectivityState.Online, true, AgentOperationalStatus.Disabled)]
    [InlineData(AgentStatus.Revoked, AgentConnectivityState.Online, false, AgentOperationalStatus.Revoked)]
    [InlineData(AgentStatus.Active, AgentConnectivityState.Online, false, AgentOperationalStatus.VersionMismatch)]
    [InlineData(AgentStatus.Active, AgentConnectivityState.Offline, false, AgentOperationalStatus.VersionMismatch)]
    [InlineData(AgentStatus.Active, AgentConnectivityState.Online, true, AgentOperationalStatus.Online)]
    [InlineData(AgentStatus.Active, AgentConnectivityState.Online, null, AgentOperationalStatus.Online)]
    [InlineData(AgentStatus.Active, AgentConnectivityState.Offline, true, AgentOperationalStatus.Offline)]
    [InlineData(AgentStatus.Active, AgentConnectivityState.Unknown, null, AgentOperationalStatus.Unknown)]
    [InlineData(AgentStatus.PendingEnrollment, AgentConnectivityState.Online, true, AgentOperationalStatus.Unknown)]
    public void LifecycleOverridesCompatibilityWhichOverridesLiveness(
        AgentStatus lifecycle, AgentConnectivityState connectivity, bool? protocolSupported, AgentOperationalStatus expected) =>
        Assert.Equal(expected, AgentConnectivityEvaluator.EvaluateOperational(lifecycle, connectivity, protocolSupported));
}

public sealed class AgentProtocolGateTests
{
    private static AgentProtocolGate Gate(int major = 1, int minMinor = 0, int maxMinor = 0) => new(new AgentSecurityOptions
    {
        Protocol = new ProtocolNegotiationContract(new ProtocolVersion(major, maxMinor),
            new ProtocolVersionRange(new ProtocolVersion(major, minMinor), new ProtocolVersion(major, maxMinor)))
    });

    [Fact]
    public void ADeclaredVersionInsideTheWindowIsCompatible()
    {
        var decision = Gate(1, 0, 2).Evaluate("1.1");
        Assert.True(decision.IsCompatible);
        Assert.Equal(new ProtocolVersion(1, 1), decision.Version);
        Assert.Null(decision.ToError());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AMissingDeclarationIsItsOwnFailure(string? declared)
    {
        var decision = Gate().Evaluate(declared);
        Assert.Equal(AgentProtocolOutcome.Missing, decision.Outcome);
        Assert.Equal("protocol_missing", decision.ToError()!.Value.Error);
    }

    [Theory]
    [InlineData("one.zero")]
    [InlineData("1")]
    [InlineData("0.1")]
    [InlineData("1.0.0")]
    [InlineData("-1.0")]
    public void AMalformedDeclarationIsRejectedWithoutBeingTreatedAsIncompatibility(string declared)
    {
        var decision = Gate().Evaluate(declared);
        Assert.Equal(AgentProtocolOutcome.Malformed, decision.Outcome);
        Assert.Equal("invalid_request", decision.ToError()!.Value.Error);
    }

    [Fact]
    public void AnOlderMajorMustUpgradeTheAgentAndANewerMajorMustWaitForTheServer()
    {
        var gate = Gate(major: 2);
        var tooOld = gate.Evaluate("1.0");
        var tooNew = gate.Evaluate("3.0");
        Assert.Equal(AgentProtocolOutcome.AgentTooOld, tooOld.Outcome);
        Assert.Equal(("protocol_incompatible", "upgrade_agent"), tooOld.ToError()!.Value);
        Assert.Equal(AgentProtocolOutcome.AgentTooNew, tooNew.Outcome);
        Assert.Equal(("protocol_incompatible", "wait_for_server_upgrade"), tooNew.ToError()!.Value);
    }

    [Fact]
    public void MinorVersionsOutsideTheWindowAreBoundedOnBothSides()
    {
        var gate = Gate(1, minMinor: 2, maxMinor: 4);
        Assert.Equal(AgentProtocolOutcome.AgentTooOld, gate.Evaluate("1.1").Outcome);
        Assert.True(gate.Evaluate("1.2").IsCompatible);
        Assert.True(gate.Evaluate("1.4").IsCompatible);
        Assert.Equal(AgentProtocolOutcome.AgentTooNew, gate.Evaluate("1.5").Outcome);
    }
}

public sealed class AgentConnectivityOptionsTests
{
    [Fact]
    public void DefaultsAreValidAndHeartbeatIsNotTheTimeout()
    {
        var options = new AgentConnectivityOptions();
        Assert.Empty(options.Validate());
        Assert.True(options.OfflineTimeout >= options.HeartbeatInterval * AgentConnectivityOptions.MinimumHeartbeatsPerTimeout);
    }

    [Fact]
    public void ATimeoutThatCannotTolerateMissedHeartbeatsIsRejected()
    {
        var options = new AgentConnectivityOptions { HeartbeatInterval = TimeSpan.FromSeconds(30), OfflineTimeout = TimeSpan.FromSeconds(60) };
        Assert.Contains(options.Validate(), problem => problem.Contains("OfflineTimeout", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(301)]
    public void HeartbeatIntervalIsBounded(int seconds)
    {
        var options = new AgentConnectivityOptions { HeartbeatInterval = TimeSpan.FromSeconds(seconds), OfflineTimeout = TimeSpan.FromHours(1) };
        Assert.Contains(options.Validate(), problem => problem.Contains("HeartbeatInterval", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        var options = new AgentConnectivityOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            OfflineTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromSeconds(1),
            SeenWriteCoalescing = TimeSpan.FromHours(1),
            ConnectionAuditInterval = TimeSpan.Zero
        };
        Assert.Equal(5, options.Validate().Count);
    }
}
