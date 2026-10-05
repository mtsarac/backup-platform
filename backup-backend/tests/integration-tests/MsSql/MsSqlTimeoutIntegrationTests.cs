using System.Diagnostics;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlTimeoutIntegrationTests(MsSqlFixture fixture)
{
    [Fact]
    public async Task CommandTimeoutEndsALongRunningStatementAndIsClassifiedAsTimeout()
    {
        var settings = WithTimeouts(commandTimeout: TimeSpan.FromSeconds(1));
        await using var session = await SqlServerSession.OpenAsync(settings, fixture.Admin, NullLogger.Instance, CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<DatabaseProviderException>(() => session.QueryAsync(
            "wait", "WAITFOR DELAY '00:00:30'; SELECT 1;", bind: null, reader => reader.GetInt32(0), CancellationToken.None));
        stopwatch.Stop();

        Assert.Equal(DatabaseFailureKind.Timeout, failure.Kind);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellingTheTokenInterruptsARunningStatementPromptly()
    {
        var settings = WithTimeouts(commandTimeout: TimeSpan.FromSeconds(60));
        await using var session = await SqlServerSession.OpenAsync(settings, fixture.Admin, NullLogger.Instance, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync(
            "wait", "WAITFOR DELAY '00:00:30'; SELECT 1;", bind: null, reader => reader.GetInt32(0), cancellation.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task CancellationNeverSurfacesAsAProviderFailure()
    {
        var provider = new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DiscoverAsync(fixture.Settings, fixture.Admin, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.PreCheckFullBackupAsync(fixture.Settings, fixture.Admin, new FullBackupPreCheckRequest(fixture.OnlineFull), cancelled.Token));
    }

    [Fact]
    public async Task DiscoveryCancelledInFlightReturnsPromptly()
    {
        var provider = new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance);
        using var cancellation = new CancellationTokenSource();

        var stopwatch = Stopwatch.StartNew();
        var discovery = provider.DiscoverAsync(fixture.Settings, fixture.Admin, cancellation.Token);
        await cancellation.CancelAsync();
        try
        {
            await discovery;
        }
        catch (OperationCanceledException)
        {
            // Either outcome is legitimate: the cancel can race the discovery to completion.
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Discovery took {stopwatch.Elapsed}.");
    }

    private DatabaseConnectionSettings WithTimeouts(TimeSpan commandTimeout) => new(
        fixture.Settings.Endpoint,
        new DatabaseConnectionOptions(
            connectTimeout: fixture.Settings.Options.ConnectTimeout,
            commandTimeout: commandTimeout,
            requireEncryption: fixture.Settings.Options.RequireEncryption,
            trustServerCertificate: fixture.Settings.Options.TrustServerCertificate));
}
