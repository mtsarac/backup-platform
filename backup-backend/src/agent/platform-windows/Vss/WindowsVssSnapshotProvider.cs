using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using BackupPlatform.SnapshotProviders.Abstractions;

namespace BackupPlatform.Agent.Platform.Windows.Vss;

/// <summary>
/// Point-in-time view of a volume through the native VSS requester API, so the agent service never needs administrator rights: Backup Operators
/// is enough. The snapshot is created with the auto-release context (VSS_CTX_BACKUP): the requester object owns it and removes it when the lease
/// is disposed. Only a snapshot this agent created is ever deleted; no other shadow copy is touched.
/// Every COM call of one snapshot runs on one dedicated MTA thread that lives as long as the lease.
/// A killed agent does NOT take its snapshot with it (observed on Windows 11), so ownership is written to a ledger file in the snapshot directory
/// before the snapshot is taken, and <see cref="SweepLeftoversAsync"/> deletes exactly the ledger's snapshots that no live lease owns.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVssSnapshotProvider : ISnapshotProvider, ISnapshotSweeper
{
    private const string LedgerDirectory = ".bp-vss";
    private const string LedgerExtension = ".owned";
    private static readonly Regex VolumeRoot = new("^[A-Za-z]:[\\\\/]$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex SnapshotName = new("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Lazy<int> ProcessSecurity = new(() => VssNative.CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 6 /* PKT_PRIVACY */, 2 /* IDENTIFY */, IntPtr.Zero, 0, IntPtr.Zero));
    // Snapshots a lease of this process still owns: a sweep never deletes one of them.
    private static readonly ConcurrentDictionary<Guid, byte> Live = new();

    public SnapshotKind Kind => SnapshotKind.Vss;

    public async Task<SnapshotLease> CreateAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SnapshotName.IsMatch(request.Name)) throw new ArgumentException("Snapshot name must be a simple unique identifier.", nameof(request));
        if (!VolumeRoot.IsMatch(request.Source)) throw new ArgumentException("VSS source must be a volume root.", nameof(request));
        if (!Path.IsPathFullyQualified(request.SnapshotDirectory)) throw new ArgumentException("Snapshot directory must be absolute.", nameof(request));

        var session = new Session(request.Source.Replace('/', '\\'), Path.Combine(request.SnapshotDirectory, LedgerDirectory));
        using var cancellation = cancellationToken.Register(session.Cancel);
        var path = await session.StartAsync().ConfigureAwait(false);
        var lease = new SnapshotLease(path, session.ReleaseAsync);
        if (!cancellationToken.IsCancellationRequested) return lease;
        await lease.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return lease;
    }

    /// <summary>
    /// Deletes the snapshots in the ledger that no live lease owns: what a killed agent left behind. A ledger entry whose snapshot is already gone is dropped.
    /// A failure on one entry does not stop the others; all are rethrown together. Returns the number of snapshots removed.
    /// </summary>
    public Task<int> SweepLeftoversAsync(string snapshotDirectory, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(snapshotDirectory)) throw new ArgumentException("Snapshot directory must be absolute.", nameof(snapshotDirectory));
        var ledger = Path.Combine(snapshotDirectory, LedgerDirectory);
        if (!Directory.Exists(ledger)) return Task.FromResult(0);
        // A ledger anybody else could write is not proof of ownership: refuse to act on it at all.
        ProtectedLedger.Verify(ledger);
        return OnComThread(() =>
        {
            var removed = 0;
            var failures = new List<Exception>();
            foreach (var entry in Directory.EnumerateFiles(ledger, "*" + LedgerExtension))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(entry), "N", out var id) || Live.ContainsKey(id)) continue;
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 || !ProtectedLedger.IsEntry(entry)) continue;
                try
                {
                    using var requester = Requester.Open(VssNative.ContextAll);
                    if (requester.Delete(id)) removed++;
                    File.Delete(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { failures.Add(new IOException("A leftover VSS snapshot could not be removed.", exception)); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
            return removed;
        });
    }

    private static Task<T> OnComThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            var apartment = VssNative.CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */) >= 0;
            try { _ = ProcessSecurity.Value; done.SetResult(work()); }
            catch (Exception exception) { done.SetException(exception); }
            finally { if (apartment) VssNative.CoUninitialize(); }
        }) { IsBackground = true, Name = "vss-sweep" }.Start();
        return done.Task;
    }

    /// <summary>One initialized requester; releasing it releases (and, for the auto-release context, deletes) its snapshots.</summary>
    private sealed class Requester : IDisposable
    {
        private readonly IVssBackupComponents components;
        private Requester(IVssBackupComponents components) => this.components = components;
        public IVssBackupComponents Components => components;

        public static Requester Open(int context)
        {
            IVssBackupComponents components;
            try
            {
                Check(VssNative.CreateVssBackupComponents(out var raw), "create requester");
                try { components = (IVssBackupComponents)Marshal.GetObjectForIUnknown(raw); }
                finally { Marshal.Release(raw); }
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
            { throw new SnapshotException(SnapshotFailureKind.Failed, "The VSS requester is not available on this system.", exception); }
            try
            {
                Check(components.InitializeForBackup(null), "initialize");
                Check(components.SetContext(context), "set context");
                Check(components.SetBackupState(false, false, VssNative.BackupTypeCopy, false), "set backup state");
                return new Requester(components);
            }
            catch { Marshal.FinalReleaseComObject(components); throw; }
        }

        /// <summary>
        /// Tells the writers the backup finished. This is a copy backup with no components selected, so it truncates nothing; it is the protocol's closing
        /// notification and its failure never fails a backup that already read its data.
        /// </summary>
        public void Complete()
        {
            try
            {
                if (components.BackupComplete(out var async) < 0) return;
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline && async.QueryStatus(out var status, out _) >= 0 && status == VssNative.AsyncPending) _ = async.Wait(250);
                }
                finally { Marshal.FinalReleaseComObject(async); }
            }
            catch (COMException) { }
        }

        /// <summary>Tells the writers the backup did not finish (creation failed or was cancelled).</summary>
        public void Abort() { try { _ = components.AbortBackup(); } catch (COMException) { } }

        /// <summary>Deletes one snapshot by id; false when it no longer exists.</summary>
        public bool Delete(Guid snapshot)
        {
            var result = components.DeleteSnapshots(snapshot, VssNative.ObjectSnapshot, true, out var deleted, out _);
            if (result == VssNative.ObjectNotFound) return false;
            Check(result, "delete snapshot");
            return deleted > 0;
        }

        public void Dispose() => Marshal.FinalReleaseComObject(components);
    }

    private sealed class Session(string volume, string ledger)
    {
        private readonly TaskCompletionSource<string> created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new();
        private volatile bool cancelled;

        public void Cancel() => cancelled = true;

        public Task<string> StartAsync()
        {
            new Thread(Run) { IsBackground = true, Name = "vss-snapshot" }.Start();
            return created.Task;
        }

        /// <summary>Deletes the snapshot and waits for the owning thread; a failed deletion is reported to the caller.</summary>
        public Task ReleaseAsync()
        {
            release.Set();
            return finished.Task;
        }

        private void Run()
        {
            Requester? requester = null;
            Guid snapshot = default;
            string? entry = null;
            Exception? cleanupFailure = null;
            var failed = false;
            var apartment = VssNative.CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */) >= 0;
            try
            {
                _ = ProcessSecurity.Value; // already set by another component of the process is fine: RPC_E_TOO_LATE is ignored
                requester = Requester.Open(VssNative.ContextBackup);
                var path = Create(requester.Components, id =>
                {
                    snapshot = id;
                    Live[id] = 0;
                    ProtectedLedger.Ensure(ledger);
                    entry = Path.Combine(ledger, id.ToString("N") + LedgerExtension);
                    File.WriteAllText(entry, ProtectedLedger.Marker + "\n" + DateTimeOffset.UtcNow.ToString("O"));
                });
                created.SetResult(path);
                release.Wait();
            }
            catch (Exception exception) { failed = true; created.TrySetException(exception); }
            finally
            {
                if (requester is not null)
                {
                    // Explicit delete (not only the auto-release of the context): the result is reported to the caller and the ledger entry is dropped only once the snapshot is gone.
                    try { if (failed) requester.Abort(); else requester.Complete(); if (snapshot != default) requester.Delete(snapshot); if (entry is not null) File.Delete(entry); }
                    catch (Exception exception) { cleanupFailure = exception; }
                    finally { requester.Dispose(); }
                }
                else if (entry is not null) { try { File.Delete(entry); } catch (IOException) { } }
                if (snapshot != default && cleanupFailure is null) Live.TryRemove(snapshot, out _);
                if (apartment) VssNative.CoUninitialize();
                if (cleanupFailure is null) finished.SetResult(); else finished.SetException(cleanupFailure);
            }
        }

        private string Create(IVssBackupComponents components, Action<Guid> owned)
        {
            Check(components.GatherWriterMetadata(out var gather), "gather writers"); Await(gather, "gather writers");
            Check(components.StartSnapshotSet(out _), "start set");
            Check(components.AddToSnapshotSet(volume, Guid.Empty, out var snapshot), "add volume");
            owned(snapshot); // ledger first: a kill from here on is swept at the next start
            Check(components.PrepareForBackup(out var prepare), "prepare"); Await(prepare, "prepare");
            Check(components.DoSnapshotSet(out var take), "take snapshot"); Await(take, "take snapshot");
            Check(components.GetSnapshotProperties(snapshot, out var properties), "read snapshot");
            try { return Marshal.PtrToStringUni(properties.SnapshotDeviceObject) is { Length: > 0 } device ? device + "\\" : throw new SnapshotException(SnapshotFailureKind.Failed, "VSS returned no snapshot device."); }
            finally { VssNative.FreeSnapshotProperties(ref properties); }
        }

        private void Await(IVssAsync async, string operation)
        {
            try
            {
                while (true)
                {
                    _ = async.Wait(250);
                    Check(async.QueryStatus(out var status, out _), operation);
                    if (status != VssNative.AsyncPending)
                    {
                        if (status != VssNative.AsyncFinished) throw VssResult.ToException(status, operation);
                        return;
                    }
                    if (cancelled) { _ = async.Cancel(); throw new OperationCanceledException(); }
                }
            }
            finally { Marshal.FinalReleaseComObject(async); }
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw VssResult.ToException(result, operation);
    }
}
