using BackupPlatform.Application.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>
/// One backend node's scheduling stack with the REAL Quartz scheduler on the shared PostgreSQL store, composed exactly as the API host
/// composes it (<c>AddBackupScheduling</c>). Nothing is started until <see cref="StartAsync"/>: reconciliation and edits work against the
/// persistent store whether or not this node's scheduler is running.
/// </summary>
internal sealed class QuartzNode : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private bool _started;
    private bool _disposed;

    private QuartzNode(ServiceProvider provider) => _provider = provider;

    public static QuartzNode Create(PostgreSqlFixture database, TimeProvider? time = null, IReadOnlyDictionary<string, string?>? settings = null, IBackupWorkSignal? signal = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The sweep is parked unless a test wants it: a run that appears was woken by Quartz, not by a poll.
            ["Backups:Scheduling:ReconcileInterval"] = "01:00:00",
            ["Backups:Scheduling:AllowSecondPrecisionCron"] = "true"
        }.Concat(settings ?? new Dictionary<string, string?>()).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value)).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddSingleton(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(database.ConnectionString) { MaxPoolSize = 12 }.ConnectionString).Options);
        services.AddSingleton<IBackupAdmissionPolicy>(AdmitAllBackupAdmission.Instance);
        services.AddScoped<IBackupRunStore, PostgreSqlBackupRunStore>();
        services.AddSingleton(signal ?? new RecordingSignal());
        services.AddBackupScheduling();
        return new QuartzNode(services.BuildServiceProvider());
    }

    /// <summary>Runs <paramref name="action"/> with a fresh use-case scope, as a request or a wake-up would.</summary>
    public async Task<T> WithServiceAsync<T>(Func<BackupScheduleService, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<BackupScheduleService>());
    }

    public IBackupScheduleCoordinator Coordinator => _provider.GetRequiredService<IBackupScheduleCoordinator>();

    /// <summary>Starts this node's scheduler and sweep loop (the hosted services of the API host).</summary>
    public async Task StartAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>()) await hosted.StartAsync(CancellationToken.None);
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            foreach (var hosted in _provider.GetServices<IHostedService>().Reverse()) await hosted.StopAsync(CancellationToken.None);
        }
        await _provider.DisposeAsync();
    }

    /// <summary>What the scheduler store holds for a job, straight from its tables.</summary>
    public static async Task<IReadOnlyList<QuartzWakeUp>> WakeUpsAsync(PostgreSqlFixture database, Guid? jobId = null)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT trigger_name, job_name, start_time, next_fire_time, trigger_state, trigger_type
            FROM quartz.qrtz_triggers WHERE trigger_group = 'backup-schedule' AND (@job::text IS NULL OR job_name = @job)
            ORDER BY trigger_name
            """, connection);
        command.Parameters.AddWithValue("job", jobId is { } id ? id.ToString("N") : DBNull.Value);
        var wakeUps = new List<QuartzWakeUp>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            wakeUps.Add(new QuartzWakeUp(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2) / 10_000 - 62_135_596_800_000),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3) / 10_000 - 62_135_596_800_000), reader.GetString(4), reader.GetString(5)));
        }
        return wakeUps;
    }

    public static async Task<IReadOnlyList<string>> JobDetailsAsync(PostgreSqlFixture database, Guid? jobId = null)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT job_name FROM quartz.qrtz_job_details WHERE job_group = 'backup-schedule' AND (@job::text IS NULL OR job_name = @job) ORDER BY job_name", connection);
        command.Parameters.AddWithValue("job", jobId is { } id ? id.ToString("N") : DBNull.Value);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    public static async Task<long> ScalarAsync(PostgreSqlFixture database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task ExecuteAsync(PostgreSqlFixture database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed record QuartzWakeUp(string TriggerName, string JobName, DateTimeOffset StartTime, DateTimeOffset? NextFireTime, string State, string Type);
