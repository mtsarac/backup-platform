using BackupPlatform.Application.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Quartz;

namespace BackupPlatform.Infrastructure.Scheduling;

public static class SchedulingServiceCollectionExtensions
{
    /// <summary>Quartz's tables live in this PostgreSQL schema, apart from every domain table (created by the <c>QuartzSchedulerStore</c> migration).</summary>
    public const string QuartzSchema = "quartz";

    /// <summary>
    /// Scheduled backups: the schedule store, the use-case, a persistent clustered Quartz scheduler and the runtime that drives it.
    /// Configuration is read lazily, at first resolution, never at registration. The host must also register an <see cref="IBackupWorkSignal"/>.
    /// </summary>
    public static IServiceCollection AddBackupScheduling(this IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Backups:Scheduling").Get<BackupSchedulingOptions>() ?? new BackupSchedulingOptions();
            options.ThrowIfInvalid();
            return options;
        });
        services.AddScoped<IBackupScheduleStore, PostgreSqlBackupScheduleStore>();
        services.AddScoped<BackupScheduleService>();
        services.AddQuartz(quartz =>
        {
            quartz.ConfigureScheduler(scheduler =>
            {
                scheduler.InstanceName = "BackupPlatformScheduler";
                // Every backend node names itself, so nodes sharing the store form one cluster instead of colliding on one id.
                scheduler.GenerateInstanceId = true;
            });
            quartz.UseDefaultThreadPool(4);
            quartz.UsePersistentStore(store =>
            {
                store.UsePostgres(source => source.DataSourceFactory = provider => NpgsqlDataSource.Create(ConnectionString(provider)));
                store.ConfigureStore(options =>
                {
                    options.TablePrefix = QuartzSchema + ".qrtz_";
                    options.MisfireThreshold = TimeSpan.FromSeconds(10);
                    options.StoreJobDataAsStrings = true;
                });
                store.UseClustering(cluster => cluster.CheckinInterval = TimeSpan.FromSeconds(10));
            });
        });
        services.AddSingleton<IBackupScheduleCoordinator, QuartzBackupScheduleCoordinator>();
        services.AddSingleton<BackupSchedulerRuntime>();
        services.AddHostedService(provider => provider.GetRequiredService<BackupSchedulerRuntime>());
        return services;
    }

    private static string ConnectionString(IServiceProvider provider)
    {
        using var context = PlatformDbContext.CreateForPlatformOperations(provider.GetRequiredService<DbContextOptions<PlatformDbContext>>());
        return context.Database.GetConnectionString() ?? throw new InvalidOperationException("The platform database connection string is required by the scheduler.");
    }
}
