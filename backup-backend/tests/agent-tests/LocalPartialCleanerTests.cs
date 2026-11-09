using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class LocalPartialCleanerTests : IDisposable
{
    private static readonly TimeSpan Age = TimeSpan.FromHours(12);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_partials_" + Guid.NewGuid().ToString("N")[..10]);
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly Guid _tenant = Guid.NewGuid(), _job = Guid.NewGuid(), _run = Guid.NewGuid();

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private LocalStorageProvider Volume(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        return new(new LocalStorageOptions(root));
    }

    private static StorageObjectKey FinalKey(string scope, string name) => new($"{scope}/{name}");

    private StorageObjectKey KeyOf(Guid attempt, string name = "db_20261101T000000Z_abcdef12_FULL.bak") =>
        FinalKey(ManagedBackupLayout.AttemptScope(_tenant, _job, _run, attempt), name);

    /// <summary>Starts a checkpointed upload and cancels it half way, exactly what a killed agent leaves behind.</summary>
    private async Task<string> LeavePartialAsync(string name, StorageObjectKey key, bool age = true)
    {
        var volume = Volume(name);
        string? state = null;
        using var cancel = new CancellationTokenSource();
        var options = new StoragePutOptions(StorageOverwritePolicy.Fail)
        {
            RecordCheckpointAsync = (checkpoint, _) => { state = checkpoint.ProviderState; return Task.CompletedTask; }
        };
        var content = new CancelAfterStream(64 * 1024, cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => volume.PutAsync(key, content, options, null, cancel.Token));
        var path = Directory.GetFiles(Dir(name, key), ".*.partial").Single();
        if (age) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return state!;
    }

    private string Dir(string volume, StorageObjectKey key) => Path.GetDirectoryName(Path.Combine(_root, volume, key.Value))!;

    private LocalPartialCleaner Cleaner(LocalVolumes volumes, Func<Guid, bool> terminal) =>
        new(volumes, (id, _) => Task.FromResult(terminal(id)), _time, NullLogger<LocalPartialCleaner>.Instance);

    [Fact]
    public async Task A_terminal_attempts_partial_is_removed_from_the_default_volume_and_a_second_volume_but_another_attempts_partial_and_a_paused_ones_stay()
    {
        var def = Volume("default");
        var second = Volume("second");
        var volumes = new LocalVolumes(def, new Dictionary<string, IStorageProvider> { ["external"] = second });
        Guid finishedA = Guid.NewGuid(), finishedB = Guid.NewGuid(), paused = Guid.NewGuid();
        var keyA = KeyOf(finishedA);
        var keyB = KeyOf(finishedB);
        var keyP = KeyOf(paused);
        await LeavePartialAsync("default", keyA);
        await LeavePartialAsync("second", keyB);
        await LeavePartialAsync("default", keyP);
        // not ours: an operator's own dot-file in the same directory
        var foreign = Path.Combine(Dir("default", keyA), ".mine.partial");
        File.WriteAllText(foreign, "x");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));

        var deleted = await Cleaner(volumes, id => id != paused).RunAsync(Age, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal([foreign], Directory.GetFiles(Dir("default", keyA), ".*.partial").Where(f => f.EndsWith(".mine.partial")));
        Assert.Single(Directory.GetFiles(Dir("default", keyA), ".*.partial"));
        Assert.Empty(Directory.GetFiles(Dir("second", keyB), ".*.partial"));
        Assert.Single(Directory.GetFiles(Dir("default", keyP), ".*.partial"));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task A_young_partial_is_kept_even_when_its_attempt_is_terminal()
    {
        var def = Volume("default");
        var key = KeyOf(Guid.NewGuid());
        await LeavePartialAsync("default", key, age: false);

        var deleted = await Cleaner(new LocalVolumes(def, new Dictionary<string, IStorageProvider>()), _ => true).RunAsync(Age, CancellationToken.None);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task AbortTransfer_removes_only_the_retained_file_of_that_key_and_never_reaches_another_attempts_file_and_refuses_a_malformed_state()
    {
        var def = Volume("default");
        var key = KeyOf(Guid.NewGuid());
        var other = KeyOf(Guid.NewGuid());
        var state = await LeavePartialAsync("default", key, age: false);
        var otherState = await LeavePartialAsync("default", other, age: false);

        await def.AbortTransferAsync(key, state, CancellationToken.None);
        await def.AbortTransferAsync(key, state, CancellationToken.None); // already gone is success

        Assert.Empty(Directory.GetFiles(Dir("default", key), ".*.partial"));
        Assert.Single(Directory.GetFiles(Dir("default", other), ".*.partial"));
        // another attempt's retained name resolves inside THIS key's directory, so it can never reach that attempt's file
        await def.AbortTransferAsync(key, otherState, CancellationToken.None);
        Assert.Single(Directory.GetFiles(Dir("default", other), ".*.partial"));
        await Assert.ThrowsAsync<StorageException>(() => def.AbortTransferAsync(key, "../" + otherState, CancellationToken.None));
        await Assert.ThrowsAsync<StorageException>(() => def.AbortTransferAsync(key, ".other.partial", CancellationToken.None));
    }

    /// <summary>Delivers a fixed number of bytes, then fires the token, so an upload stops with a partial file of known size.</summary>
    private sealed class CancelAfterStream(int bytes, CancellationTokenSource cancel) : Stream
    {
        private int _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent >= bytes) { cancel.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(0); }
            var n = Math.Min(buffer.Length, bytes - _sent);
            buffer.Span[..n].Fill(7);
            _sent += n;
            return ValueTask.FromResult(n);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
