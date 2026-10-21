using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Host;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.AgentTests;

public sealed class DeleteReplicaCompositionTests
{

    [Fact]
    public void Missing_local_configuration_is_explicit_not_a_fallback_to_another_storage()
    {
        using var provider = Build(null);
        var resolution = provider.GetRequiredService<IDeleteReplicaProviderResolver>().Resolve("local", "local");
        Assert.Null(resolution.Provider);
        Assert.Equal(DeleteReplicaFailureCodes.InvalidConfiguration, resolution.FailureCode);
    }

    [Fact]
    public void Ambiguous_local_configuration_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-deletion-composition");
        var resolver = new DeleteReplicaProviderResolver([
            new LocalStorageProvider(new LocalStorageOptions(root)),
            new LocalStorageProvider(new LocalStorageOptions(root + "-other"))]);
        Assert.Equal(DeleteReplicaFailureCodes.InvalidConfiguration, resolver.Resolve("local", "local").FailureCode);
    }

    [Fact]
    public async Task Deletion_dispatch_runs_while_the_single_backup_slot_is_occupied()
    {
        await using var backups = new BackupTaskHarness();
        await using var deletions = new DeleteReplicaHarness();
        await using var restores = new RestoreTaskHarness();
        await using var downloads = new DownloadTaskHarness();
        using var provider = Build(null);
        var dispatcher = new AgentTaskDispatcher(backups.Worker, restores.Worker, deletions.Worker, downloads.Worker, restores.CleanupWorker,
            provider.GetRequiredService<CapacityProbeTaskWorker>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backups.Pipeline.Execute = async (request, _, token) =>
        {
            await release.Task.WaitAsync(token);
            return backups.Success(request);
        };
        backups.Start();
        deletions.Start();
        Assert.True(dispatcher.TryPublish(backups.Envelope()));
        await Wait.UntilAsync(() => backups.Pipeline.Calls.Count == 1, "occupied backup slot");
        var deletion = deletions.Envelope();
        await deletions.PutAsync(deletion.DeleteReplica!);
        Assert.True(dispatcher.TryPublish(deletion));
        Assert.False(dispatcher.TryPublish(new AgentTaskEnvelope { Type = "unknown" }));
        await Wait.UntilAsync(() => deletions.Client.Completions.Count == 1, "independent deletion completion");
        Assert.Single(backups.Pipeline.Calls);
        Assert.Empty(backups.Client.Completions);
        Assert.True(Assert.Single(deletions.Client.Completions).AbsenceVerified);
        release.SetResult();
        await Wait.UntilAsync(() => backups.Client.Completions.Count == 1, "unchanged backup completion");
    }

    private static ServiceProvider Build(string? root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Backend:BaseUri"] = "https://backup.example.test/",
            ["Agent:Backup:LocalStorage:RootDirectory"] = root
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(configuration);
        return services.BuildServiceProvider();
    }
}
