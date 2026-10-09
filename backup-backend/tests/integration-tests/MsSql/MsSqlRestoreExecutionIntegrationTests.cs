using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.IntegrationTests.MsSql;

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlRestoreExecutionIntegrationTests(MsSqlFixture fixture) : IAsyncLifetime
{
    private readonly MsSqlRestoreHarness _harness = new(fixture);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task NewDatabaseWithDifferentNameUsesTargetDirectoriesAndNeverReplace()
    {
        var source = await _harness.FullBackupAsync();
        var target = _harness.NewName("new");
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.NewDatabase, verify: true);
        var reports = new List<RestoreProgress>();
        foreach (var mapping in request.Plan.Mappings)
        {
            var directory = mapping.Kind == RestoreFileKind.Log ? request.Plan.Target.DefaultLogDirectory : request.Plan.Target.DefaultDataDirectory;
            Assert.StartsWith(directory.Value.TrimEnd('/', '\\') + (directory.Value.Contains('\\') ? "\\" : "/"), mapping.TargetPath.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(mapping.TargetPath.Value, request.Inspection.Files.Select(file => file.SourcePhysicalName));
        }

        var result = await _harness.ExecuteAsync(request, new InlineProgress(reports.Add));

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.Online, result.TargetState);
        Assert.Equal("ONLINE", result.DatabaseState);
        Assert.Equal("MULTI_USER", (await _harness.StateAsync(target))!.Value.Access);
        Assert.Equal(MsSqlRestoreHarness.SourceRows, await _harness.CountNotesAsync(target));
        Assert.False(await _harness.LastRestoreUsedReplaceAsync(target));
        var actualFiles = await _harness.FilesAsync(target);
        foreach (var mapping in request.Plan.Mappings)
        {
            Assert.Equal(mapping.TargetPath.Value, Assert.Single(actualFiles, file => file.Logical == mapping.LogicalName).Path);
        }
        Assert.Contains(reports, report => report.Stage == RestoreExecutionStage.PreparingTarget);
        Assert.Contains(reports, report => report.Stage == RestoreExecutionStage.Restoring && report.Percent == 100);
        Assert.Contains(reports, report => report.Stage == RestoreExecutionStage.Finalizing);
        Assert.Equal(MsSqlRestoreHarness.SourceRows, await _harness.CountNotesAsync(fixture.Source));
    }

    [Fact]
    public async Task ExplicitReplacementOverwritesContentsAndDisconnectsOnlyWhenAuthorized()
    {
        var source = await _harness.FullBackupAsync();
        var target = await _harness.CreateTargetAsync("replace", 7);
        await using var held = await _harness.HoldSessionAsync(target);
        var blocked = await _harness.Provider.PreCheckRestoreAsync(fixture.Settings, fixture.Admin,
            new RestorePreCheckRequest(target, DatabaseRestoreMode.ReplaceExistingDatabase, false, source.SizeBytes), CancellationToken.None);
        Assert.Contains(blocked.Items, item => item.Status == BackupPreCheckStatus.Failed && item.ReasonCode == RestoreReasonCodes.TargetInUse);
        Assert.True(await held.IsAliveAsync());
        Assert.Equal(7, await _harness.CountNotesAsync(target));
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.ReplaceExistingDatabase, terminateActiveConnections: true);

        var result = await _harness.ExecuteAsync(request);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.True(await _harness.LastRestoreUsedReplaceAsync(target));
        Assert.False(await held.IsAliveAsync());
        Assert.Equal("MULTI_USER", (await _harness.StateAsync(target))!.Value.Access);
        Assert.Equal(MsSqlRestoreHarness.SourceRows, await _harness.CountNotesAsync(target));
        Assert.False(await _harness.TableExistsAsync(target, "OnlyInTarget"));
        Assert.Contains(result.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.SessionsDisconnected);
    }

    [Fact]
    public async Task ReplacementReusesFilesWithMatchingLogicalNames()
    {
        var source = await _harness.FullBackupAsync();
        var target = _harness.NewName("inplace");
        var first = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.NewDatabase);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, (await _harness.ExecuteAsync(first)).Status);
        var original = await _harness.FilesAsync(target);
        var replacement = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.ReplaceExistingDatabase);
        foreach (var mapping in replacement.Plan.Mappings)
        {
            Assert.Equal(Assert.Single(original, file => file.Logical == mapping.LogicalName).Path, mapping.TargetPath.Value);
        }
        Assert.Contains(replacement.Plan.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.ExistingFilesReused);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, (await _harness.ExecuteAsync(replacement)).Status);
    }

    [Fact]
    public async Task NameTakenAfterPlanningIsNotOverwritten()
    {
        var source = await _harness.FullBackupAsync();
        var target = _harness.NewName("race");
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.NewDatabase);
        await fixture.ExecuteAsync($"CREATE DATABASE {MsSqlRestoreHarness.Quote(target)}");
        await _harness.ExecuteInAsync(target, "CREATE TABLE dbo.Marker (Id int)");

        var result = await _harness.ExecuteAsync(request);

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreReasonCodes.TargetDatabaseExists, result.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.True(await _harness.TableExistsAsync(target, "Marker"));
        Assert.Null(await _harness.LastRestoreUsedReplaceAsync(target));
    }

    [Fact]
    public async Task UnreadableArtifactAfterPlanningRestoresMultiUserInFinally()
    {
        var source = await _harness.FullBackupAsync();
        var target = await _harness.CreateTargetAsync("missing", 3);
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.ReplaceExistingDatabase, terminateActiveConnections: true);
        request = new DatabaseRestoreRequest(target, fixture.Staging.ServerDirectory.Combine($"bp_it_rs_missing_{Guid.NewGuid():N}.bak"),
            request.Mode, true, request.Inspection, request.Plan);
        await using var held = await _harness.HoldSessionAsync(target);

        var result = await _harness.ExecuteAsync(request);

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(SqlServerRestoreReasons.BackupFileUnreadable, result.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Equal("MULTI_USER", (await _harness.StateAsync(target))!.Value.Access);
        Assert.Equal(3, await _harness.CountNotesAsync(target));
        Assert.False(await held.IsAliveAsync());
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.DatabaseLeftSingleUser);
    }

    [Fact]
    public async Task PermissionFailureDuringExecutionDoesNotCreateTarget()
    {
        var source = await _harness.FullBackupAsync();
        var target = _harness.NewName("denied");
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.NewDatabase);

        var result = await _harness.ExecuteAsync(request, credential: fixture.Credential(fixture.Operator));

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(BackupFailureCause.PermissionDenied, result.Failure!.Cause);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Null(await _harness.StateAsync(target));
    }

    [Fact]
    public async Task CancellationBeforePreparationDoesNotTouchTarget()
    {
        var source = await _harness.FullBackupAsync();
        var target = _harness.NewName("earlycancel");
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.NewDatabase);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await _harness.ExecuteAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Null(await _harness.StateAsync(target));
    }

    [Fact]
    public async Task CancellationAfterSingleUserRestoresMultiUserWithoutRestoringData()
    {
        var source = await _harness.FullBackupAsync();
        var target = await _harness.CreateTargetAsync("cancelaccess", 5);
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.ReplaceExistingDatabase, terminateActiveConnections: true);
        await using var held = await _harness.HoldSessionAsync(target);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(report =>
        {
            if (report.Stage == RestoreExecutionStage.Restoring)
            {
                cancellation.Cancel();
            }
        });

        var result = await _harness.ExecuteAsync(request, progress, cancellationToken: cancellation.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Equal("MULTI_USER", (await _harness.StateAsync(target))!.Value.Access);
        Assert.Equal(5, await _harness.CountNotesAsync(target));
        Assert.False(await held.IsAliveAsync());
        Assert.Null(result.RestoreStartedAt);
    }

    [Fact]
    public async Task CancellationOfRunningRestoreReportsActualPartialStateAndStopsServerCommand()
    {
        var source = await _harness.BigBackupAsync();
        var target = _harness.NewName("runningcancel");
        var request = await _harness.BuildRequestAsync(source, target, DatabaseRestoreMode.NewDatabase);
        using var cancellation = new CancellationTokenSource();
        var execution = _harness.ExecuteAsync(request, cancellationToken: cancellation.Token);
        using var observationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        DatabaseRestoreResult result;
        try
        {
            while (await _harness.CountRunningRestoresAsync(target) == 0 || (await _harness.StateAsync(target))?.State != "RESTORING")
            {
                Assert.False(execution.IsCompleted, "The server must be observed actively restoring before cancellation.");
                await Task.Delay(25, observationTimeout.Token);
            }
            cancellation.Cancel();
            result = await execution;
        }
        finally
        {
            await cancellation.CancelAsync();
            await execution;
        }
        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, result.TargetState);
        var state = await _harness.StateAsync(target);
        Assert.NotNull(state);
        Assert.Equal("RESTORING", state.Value.State);
        Assert.Equal(state.Value.State, result.DatabaseState);
        Assert.Contains(result.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.DatabaseLeftRestoring);
        Assert.Equal(0, await _harness.CountRunningRestoresAsync(target));
    }

    private sealed class InlineProgress(Action<RestoreProgress> report) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => report(value);
    }
}
