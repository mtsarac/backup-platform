using System.Text.Json;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Restores;

namespace BackupPlatform.AgentTests;

public sealed class RestoreTaskResultMapperTests
{
    private static RestoreTaskSnapshot Task() => new RestoreTaskHarness().Envelope().Restore!;

    private static DatabaseRestorePipelineResult Result(Func<RestoreTaskHarness, DatabaseRestorePipelineResult> build)
    {
        var harness = new RestoreTaskHarness();
        return build(harness);
    }

    [Fact]
    public void A_code_outside_the_vocabulary_becomes_an_internal_error()
    {
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness => harness.Failed("SomethingNew")));

        Assert.Equal("InternalError", mapped.FailureCode);
    }

    [Fact]
    public void Cancellation_is_reported_as_cancelled_whatever_code_the_failure_carries()
    {
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness =>
            harness.Failed(RestoreAgentFailureCodes.SqlRestoreFailed, DatabaseRestoreTargetState.PartiallyRestored, DatabaseRestoreStatus.Cancelled)));

        Assert.Equal("Cancelled", mapped.Status);
        Assert.Equal("Cancelled", mapped.FailureCode);
        Assert.Equal("PartiallyRestored", mapped.TargetState);
    }

    [Fact]
    public void A_success_that_is_not_online_is_never_reported_as_one()
    {
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness =>
            harness.Success() with { TargetState = DatabaseRestoreTargetState.Unknown }));

        Assert.Equal("Failed", mapped.Status);
        Assert.Equal("InternalError", mapped.FailureCode);
        Assert.Equal("Unknown", mapped.TargetState);
    }

    [Fact]
    public void A_failure_cannot_claim_an_online_database()
    {
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness =>
            harness.Failed(RestoreAgentFailureCodes.SqlRestoreFailed, DatabaseRestoreTargetState.Online)));

        Assert.Equal("Unknown", mapped.TargetState);
    }

    [Theory]
    [InlineData("ONLINE", "ONLINE")]
    [InlineData("RECOVERY_PENDING", "RECOVERY_PENDING")]
    [InlineData("online", null)]
    [InlineData("ONLINE'; DROP", null)]
    [InlineData("", null)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFG", null)]
    public void The_database_state_is_sent_only_when_it_looks_like_an_engine_state_name(string state, string? expected)
    {
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness => harness.Success() with { DatabaseState = state }));

        Assert.Equal(expected, mapped.Result!.DatabaseState);
    }

    [Fact]
    public void Free_text_is_stripped_of_control_characters_and_cut_to_a_fixed_length()
    {
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness => harness.Success() with
        {
            PreCheck = [new BackupPreCheckItem(new string('c', 200), BackupPreCheckStatus.Failed, new string('r', 200), "line one\r\nline two\u0000" + new string('m', 500))],
            Warnings = [new BackupWarning("code", "tab\there")]
        }));

        var item = Assert.Single(mapped.Result!.PreCheck);
        Assert.Equal(64, item.CheckId.Length);
        Assert.Equal(64, item.ReasonCode.Length);
        Assert.Equal(300, item.Message.Length);
        Assert.StartsWith("line one  line two ", item.Message, StringComparison.Ordinal);
        Assert.Equal("tab here", Assert.Single(mapped.Result.Warnings).Message);
    }

    [Fact]
    public void A_result_that_is_too_large_loses_detail_in_steps_and_says_so()
    {
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness => harness.Success() with
        {
            Warnings = Enumerable.Range(0, 100).Select(i => new BackupWarning($"w{i}", new string('w', 300))).ToArray(),
            FileMappings = Enumerable.Range(0, 400)
                .Select(i => new RestoreFileMapping($"F{i}", RestoreFileKind.Data, new string('s', 400), new DatabaseServerPath("/d/" + new string('t', 390) + i), 1))
                .ToArray()
        }));

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(mapped.Result, JsonSerializerOptions.Web).Length <= RestoreTaskResultMapper.MaxResultJsonLength);
        Assert.Contains(mapped.Result!.Warnings, warning => warning.Code == RestoreTaskResultMapper.TruncatedWarning);
        Assert.NotEmpty(mapped.Result.FileMappings);
        Assert.True(mapped.Result.FileMappings.Count < 400);
    }

    [Fact]
    public void Characters_the_json_encoder_escapes_are_counted_at_their_serialized_size()
    {
        // '<' is written as \u003C: six characters on the wire. A result made of them must still fit once serialized.
        var mapped = RestoreTaskResultMapper.Map(Task(), Result(harness => harness.Success() with
        {
            Warnings = Enumerable.Range(0, 16).Select(i => new BackupWarning($"w{i}", new string('<', 300))).ToArray(),
            PreCheck = Enumerable.Range(0, 32).Select(i => new BackupPreCheckItem($"c{i}", BackupPreCheckStatus.Warning, "r", new string('<', 300))).ToArray()
        }));

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(mapped.Result, JsonSerializerOptions.Web).Length <= RestoreTaskResultMapper.MaxResultJsonLength);
    }

    [Fact]
    public void A_refused_snapshot_failure_carries_a_known_code_and_no_result_detail()
    {
        var failure = RestoreTaskResultMapper.Failure(Guid.NewGuid(), "NotACode", DatabaseRestoreTargetState.NotModified);

        Assert.Equal("Failed", failure.Status);
        Assert.Equal("InternalError", failure.FailureCode);
        Assert.Null(failure.Result!.SourceBackup);
        Assert.Empty(failure.Result.FileMappings);
    }
}
