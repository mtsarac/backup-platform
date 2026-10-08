using System.Text.Json;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

public sealed class DownloadStoreTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly RestoreWorld _world = new(fixture);
    private readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private DateTimeOffset Now => _now;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(3);
    private PostgreSqlDownloadStore Store() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    private async Task<DownloadCreateCommand> SeedAsync()
    {
        var seed = await _world.SeedCentralAsync(RetentionKit.Policy(grace: TimeSpan.FromHours(1)));
        var user = new User(UserId.New(), "Download administrator");
        await using var context = fixture.CreatePlatformContext();
        context.Users.Add(user);
        context.TenantMemberships.Add(new TenantMembership(seed.TenantId, user.Id, TenantRole.Admin));
        await context.SaveChangesAsync();
        return new(seed.TenantId, seed.ArtifactId, seed.ReplicaId, user.Id.Value, "master.bak", Now, Lifetime);
    }

    [Fact]
    public async Task CreateRejectsCrossTenantArtifactAndReplicaAndDeletePending()
    {
        var command = await SeedAsync();
        var other = await SeedAsync();
        var store = Store();
        Assert.Null(await store.CreateAsync(command with { TenantId = other.TenantId }, default));
        Assert.Null(await store.CreateAsync(command with { ReplicaId = other.ReplicaId }, default));
        Assert.Null(await store.CreateAsync(command with { ArtifactId = other.ArtifactId }, default));
        Assert.Null(await store.CreateAsync(command with { UserId = Guid.NewGuid() }, default));
        var session = await store.CreateAsync(command, default);
        Assert.NotNull(session);
        Assert.Null(await store.GetAsync(other.TenantId, session.Id, default));
        await _world.Retention.Store().RequestDeleteAsync(command.TenantId, command.ArtifactId, command.UserId, Now, default);
        Assert.Null(await store.CreateAsync(command, default));
    }

    [Theory]
    [InlineData(DownloadState.Completed)]
    [InlineData(DownloadState.Cancelled)]
    [InlineData(DownloadState.Failed)]
    public async Task DurablePinBlocksPhysicalClaimUntilTerminalTransition(DownloadState terminal)
    {
        var command = await SeedAsync();
        var store = Store();
        var session = (await store.CreateAsync(command, default))!;
        Assert.NotNull(session);
        await _world.Retention.Store().RequestDeleteAsync(command.TenantId, command.ArtifactId, command.UserId, Now, default);
        var due = (await _world.ReplicaAsync(command.ReplicaId)).DeleteAfter!.Value;
        Assert.DoesNotContain(await _world.Retention.Store().ClaimDueDeletionsAsync("node-a", due, RetentionWorld.Lease, 500, default),
            claim => claim.ReplicaId == command.ReplicaId);
        if (terminal == DownloadState.Completed)
        {
            Assert.NotNull(await store.TransitionAsync(session.Id, new(DownloadState.Ready, DownloadState.Streaming, null, "node-a", due, Lease), default));
        }
        var ended = await store.TransitionAsync(session.Id, new(terminal == DownloadState.Completed ? DownloadState.Streaming : DownloadState.Ready,
            terminal, terminal == DownloadState.Completed ? "node-a" : null, null, due.AddSeconds(1), Bytes: terminal == DownloadState.Completed ? session.SizeBytes : 0,
            FailureCode: terminal == DownloadState.Completed ? null : "download_cancelled"), default);
        Assert.NotNull(ended);
        Assert.Equal(due.AddSeconds(1), ended.ReleasedAt);
        Assert.Contains(await _world.Retention.Store().ClaimDueDeletionsAsync("node-b", due.AddSeconds(2), RetentionWorld.Lease, 500, default), claim => claim.ReplicaId == command.ReplicaId);
    }

    [Fact]
    public async Task LeaseExpiryReleasesPinAndFencesCompletionWithoutSweeper()
    {
        var command = (await SeedAsync()) with { Now = Now.AddHours(-2) };
        var store = Store();
        var session = (await store.CreateAsync(command, default))!;
        Assert.NotNull(await store.TransitionAsync(session.Id, new(DownloadState.Ready, DownloadState.Streaming, null, "node-a", command.Now, Lease), default));
        await _world.Retention.Store().RequestDeleteAsync(command.TenantId, command.ArtifactId, command.UserId, Now, default);
        var due = (await _world.ReplicaAsync(command.ReplicaId)).DeleteAfter!.Value;
        Assert.Null(await store.TransitionAsync(session.Id, new(DownloadState.Streaming, DownloadState.Completed, "node-a", null, Now, Bytes: session.SizeBytes), default));
        Assert.Contains(await _world.Retention.Store().ClaimDueDeletionsAsync("node-b", due, RetentionWorld.Lease, 500, default), claim => claim.ReplicaId == command.ReplicaId);
        Assert.True(await store.ExpireAsync(Now + Lease, default) >= 1);
        var expired = (await store.GetAsync(command.TenantId, session.Id, default))!;
        Assert.Equal(DownloadState.Failed, expired.State);
        Assert.Equal("download_interrupted", expired.FailureCode);
        Assert.NotNull(expired.ReleasedAt);
    }

    [Fact]
    public async Task SessionDeadlineReleasesPinEvenWithoutAnActiveLease()
    {
        var command = await SeedAsync();
        var store = Store();
        var session = (await store.CreateAsync(command with { Now = Now.AddHours(-2), Lifetime = TimeSpan.FromMinutes(1) }, default))!;
        await _world.Retention.Store().RequestDeleteAsync(command.TenantId, command.ArtifactId, command.UserId, Now, default);
        var due = (await _world.ReplicaAsync(command.ReplicaId)).DeleteAfter!.Value;
        Assert.Contains(await _world.Retention.Store().ClaimDueDeletionsAsync("node-a", due, RetentionWorld.Lease, 500, default), claim => claim.ReplicaId == command.ReplicaId);
        Assert.True(await store.ExpireAsync(session.ExpiresAt, default) >= 1);
        var expired = (await store.GetAsync(command.TenantId, session.Id, default))!;
        Assert.Equal("download_expired", expired.FailureCode);
        Assert.Equal(session.ExpiresAt, expired.ReleasedAt);
    }

    [Fact]
    public async Task ConcurrentStreamingClaimsHaveOneWinnerAndStaleOwnerCannotComplete()
    {
        var command = await SeedAsync();
        var session = (await Store().CreateAsync(command, default))!;
        var gate = new RaceGate();
        var first = gate.Run(() => Store().TransitionAsync(session.Id, new(DownloadState.Ready, DownloadState.Streaming, null, "first", Now, Lease), default));
        var second = gate.Run(() => Store().TransitionAsync(session.Id, new(DownloadState.Ready, DownloadState.Streaming, null, "second", Now, Lease), default));
        gate.Open();
        var results = await Task.WhenAll(first, second);
        var winner = Assert.Single(results, result => result is not null)!;
        var staleOwner = winner.LeaseOwner == "first" ? "second" : "first";
        Assert.Null(await Store().TransitionAsync(session.Id, new(DownloadState.Streaming, DownloadState.Completed, staleOwner, null, Now.AddSeconds(1), Bytes: session.SizeBytes), default));
        Assert.Equal(winner.LeaseOwner, (await Store().GetAsync(command.TenantId, session.Id, default))!.LeaseOwner);
    }

    [Fact]
    public async Task CreateRacingSoftDeleteNeverPinsAPhysicallyDeletingReplica()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var command = await SeedAsync();
            var gate = new RaceGate();
            var creating = gate.Run(() => Store().CreateAsync(command, default));
            var deleting = gate.Run(() => _world.Retention.Store().RequestDeleteAsync(command.TenantId, command.ArtifactId, command.UserId, Now, default));
            gate.Open();
            var session = await creating;
            await deleting;
            var due = (await _world.ReplicaAsync(command.ReplicaId)).DeleteAfter!.Value;
            var claims = await _world.Retention.Store().ClaimDueDeletionsAsync("node-a", due, RetentionWorld.Lease, 500, default);
            Assert.Equal(session is null, claims.Any(claim => claim.ReplicaId == command.ReplicaId));
            if (session is not null) Assert.True(session.IsPinned(due));
        }
    }

    [Fact]
    public async Task CancellationAuditRetainsPartialBytesAndActor()
    {
        var command = await SeedAsync();
        var store = Store();
        var session = (await store.CreateAsync(command, default))!;
        var partialBytes = session.SizeBytes / 2;
        Assert.InRange(partialBytes, 1, session.SizeBytes - 1);
        Assert.NotNull(await store.TransitionAsync(session.Id, new(DownloadState.Ready, DownloadState.Streaming, null, "node-a", Now, Lease), default));
        Assert.NotNull(await store.TransitionAsync(session.Id, new(DownloadState.Streaming, DownloadState.Cancelled, "node-a", null, Now.AddSeconds(1), Bytes: partialBytes, FailureCode: "download_cancelled"), default));
        await using var context = fixture.CreatePlatformContext();
        var audits = await context.AuditEvents.Where(audit => audit.TenantId == command.TenantId && audit.TargetId == session.Id.ToString()).OrderBy(audit => audit.At).ToListAsync();
        Assert.Equal(new[] { "download.cancelled", "download.requested", "download.streaming" }, audits.Select(audit => audit.Action).OrderBy(action => action));
        Assert.All(audits, audit => Assert.Equal(command.UserId, audit.ActorUserId.Value));
        using var details = JsonDocument.Parse(audits.Last().DetailsJson);
        Assert.Equal(partialBytes, details.RootElement.GetProperty("transferredBytes").GetInt64());
        Assert.Equal("Cancelled", details.RootElement.GetProperty("result").GetString());
        Assert.Equal(Now.AddSeconds(1), details.RootElement.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(session.Id, details.RootElement.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task ConcurrentPreparationOfDifferentSessionsSerializesByOwningAgent()
    {
        var seed = await _world.SeedAgentLocalAsync();
        var user = new User(UserId.New(), "Local download administrator");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Users.Add(user);
            context.TenantMemberships.Add(new TenantMembership(seed.TenantId, user.Id, TenantRole.Admin));
            await context.SaveChangesAsync();
        }
        var command = new DownloadCreateCommand(seed.TenantId, seed.ArtifactId, seed.ReplicaId, user.Id.Value, "master.bak", Now, Lifetime);
        var first = (await Store().CreateAsync(command, default))!;
        var second = (await Store().CreateAsync(command, default))!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(DownloadState.Preparing, first.State);
        Assert.Equal(DownloadState.Preparing, second.State);
        Assert.Equal(seed.AgentId.Value, first.AgentId);
        Assert.Equal(first.AgentId, second.AgentId);
        var gate = new RaceGate();
        var claimFirst = gate.Run(() => Store().TransitionAsync(first.Id,
            new(DownloadState.Preparing, DownloadState.Preparing, null, "request-a", Now, Lease), default));
        var claimSecond = gate.Run(() => Store().TransitionAsync(second.Id,
            new(DownloadState.Preparing, DownloadState.Preparing, null, "request-b", Now, Lease), default));
        gate.Open();
        var results = await Task.WhenAll(claimFirst, claimSecond);
        var winner = Assert.Single(results, result => result is not null)!;
        var loserId = winner.Id == first.Id ? second.Id : first.Id;
        var loser = (await Store().GetAsync(seed.TenantId, loserId, default))!;
        Assert.Equal(DownloadState.Preparing, loser.State);
        Assert.Null(loser.LeaseOwner);
        Assert.Null(loser.LeaseExpiresAt);
        Assert.Null(loser.ReleasedAt);
        Assert.NotNull(await Store().TransitionAsync(winner.Id,
            new(DownloadState.Preparing, DownloadState.Cancelled, winner.LeaseOwner, null, Now.AddSeconds(1), FailureCode: "download_cancelled"), default));
        var next = await Store().TransitionAsync(loser.Id,
            new(DownloadState.Preparing, DownloadState.Preparing, null, "next-request", Now.AddSeconds(2), Lease), default);
        Assert.NotNull(next);
        Assert.Equal("next-request", next.LeaseOwner);
        Assert.Equal(Now.AddSeconds(2) + Lease, next.LeaseExpiresAt);
        Assert.True(next.IsPinned(Now.AddSeconds(2)));
    }
}
