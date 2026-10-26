using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;
using SMBLibrary;
using SMBLibrary.Client;
using SmbAttributes = SMBLibrary.FileAttributes;

namespace BackupPlatform.StorageProviders.Smb;

/// <summary>
/// Reads and writes objects on an SMB share with a managed client, so the agent needs no mount privilege (Linux). Semantics mirror the
/// file-system provider used for Windows UNC paths: content goes to a hidden <c>.name.guid.partial</c> sibling, is validated against the expected
/// length and checksum and only then renamed onto the final name; reparse points are never followed; messages never contain a path or credential.
/// Every operation opens its own session (like the former mount-per-operation), a read stream owns its session until it is disposed.
/// The client uses SMB 3 encryption whenever the server offers or requires it; a server without encryption is accepted.
/// </summary>
internal sealed class SmbUserSpaceStore(SmbStorageOptions options, StorageProviderId id)
{
    private const int BlockSize = 1024 * 1024;
    private const AccessMask ReadAttributes = (AccessMask)0x80;   // FILE_READ_ATTRIBUTES
    private const AccessMask AddSubdirectory = (AccessMask)0x4;   // FILE_ADD_SUBDIRECTORY
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private readonly string[] _prefix = ["tenants", options.TenantId.ToString("N"), "connections", options.ConnectionId.ToString("N")];

    public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions put, IProgress<long>? progress, SmbCredential credential, CancellationToken token)
    {
        using var session = await SmbSession.OpenAsync(options, credential, token).ConfigureAwait(false);
        var segments = Segments(key);
        var directory = segments[..^1];
        EnsureDirectories(session.Store, directory, create: true);
        var finalPath = Join(segments);
        if (Probe(session.Store, finalPath) is not null) { if (put.Overwrite == StorageOverwritePolicy.Fail || IsDirectory(session.Store, finalPath)) throw AlreadyExists(); }
        // No resume: a retained prefix from an earlier attempt is ignored and the upload starts over under a fresh name.
        var temporaryName = $".{key.FileName}.{Guid.NewGuid():N}.partial";
        var temporaryPath = Join([.. directory, temporaryName]);
        if (put.RecordCheckpointAsync is { } record) await record(new(0, temporaryName), token).ConfigureAwait(false);
        var status = session.Store.CreateFile(out var handle, out _, temporaryPath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, SmbAttributes.Normal,
            ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SEQUENTIAL_ONLY | CreateOptions.FILE_OPEN_REPARSE_POINT, null);
        Check(status);
        var published = false;
        try
        {
            var (length, checksum) = await WriteAsync(session, handle, content, put, progress, token).ConfigureAwait(false);
            Check(session.Store.FlushFileBuffers(handle));
            var rename = new FileRenameInformationType2 { ReplaceIfExists = put.Overwrite == StorageOverwritePolicy.Replace, FileName = finalPath };
            status = session.Store.SetFileInformation(handle, rename);
            if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION) throw AlreadyExists();
            Check(status);
            published = true;
            return new StorageObjectMetadata(new StorageLocation(id, key), length, checksum, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (!published) session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            session.Store.CloseFile(handle);
        }
    }

    public async Task<bool> ExistsAsync(StorageObjectKey key, SmbCredential credential, CancellationToken token)
    {
        using var session = await SmbSession.OpenAsync(options, credential, token).ConfigureAwait(false);
        return Stat(session.Store, Segments(key)) is { IsDirectory: false };
    }

    public async Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, SmbCredential credential, CancellationToken token)
    {
        using var session = await SmbSession.OpenAsync(options, credential, token).ConfigureAwait(false);
        return Stat(session.Store, Segments(key)) is { IsDirectory: false } info
            ? new StorageObjectMetadata(new StorageLocation(id, key), info.Length, null, new DateTimeOffset(info.Modified, TimeSpan.Zero)) : null;
    }

    public async Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, SmbCredential credential, CancellationToken token)
    {
        var location = new StorageLocation(id, key);
        using var session = await SmbSession.OpenAsync(options, credential, token).ConfigureAwait(false);
        if (Stat(session.Store, Segments(key)) is not { IsDirectory: false } info)
            return new(location, StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null);
        if (info.Length != expected.SizeBytes)
            return new(location, StorageVerificationStatus.SizeMismatch, StorageVerificationMethod.ReadBack, info.Length, null);
        await using var stream = OpenRead(session, Segments(key), info.Length, ownsSession: false);
        var actual = await ContentHasher.ComputeSha256Async(stream, null, token).ConfigureAwait(false);
        return new(location, actual == expected.Checksum ? StorageVerificationStatus.Verified : StorageVerificationStatus.ChecksumMismatch, StorageVerificationMethod.ReadBack, info.Length, actual);
    }

    public async Task<Stream> OpenReadAsync(StorageObjectKey key, SmbCredential credential, CancellationToken token)
    {
        var session = await SmbSession.OpenAsync(options, credential, token).ConfigureAwait(false);
        try
        {
            var segments = Segments(key);
            if (Stat(session.Store, segments) is not { IsDirectory: false } info) throw new StorageException(StorageFailureKind.NotFound, "The stored object does not exist.");
            return OpenRead(session, segments, info.Length, ownsSession: true);
        }
        catch { session.Dispose(); throw; }
    }

    public async Task<bool> DeleteAsync(StorageObjectKey key, SmbCredential credential, CancellationToken token)
    {
        using var session = await SmbSession.OpenAsync(options, credential, token).ConfigureAwait(false);
        var segments = Segments(key);
        if (Stat(session.Store, segments) is not { IsDirectory: false }) return false;
        var status = session.Store.CreateFile(out var handle, out _, Join(segments), AccessMask.DELETE | AccessMask.SYNCHRONIZE, SmbAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_OPEN_REPARSE_POINT, null);
        if (status == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND) return false;
        Check(status);
        try { Check(session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true })); }
        finally { session.Store.CloseFile(handle); }
        return true;
    }

    private string[] Segments(StorageObjectKey key)
    {
        var all = new List<string>(_prefix);
        foreach (var segment in key.Segments)
        {
            // Characters Windows servers treat specially; the key rules already forbid dot segments and separators.
            if (segment.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || segment.EndsWith('.') || segment.EndsWith(' '))
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "The storage key is not valid on an SMB share.");
            all.Add(segment);
        }
        return [.. all];
    }

    private static string Join(IEnumerable<string> segments) => string.Join('\\', segments);

    private static StorageException AlreadyExists() => new(StorageFailureKind.AlreadyExists, "An object already exists under this key.");

    /// <summary>Opens every directory of the path without following reparse points; with <paramref name="create"/> missing ones are created.</summary>
    private static bool EnsureDirectories(INTFileStore store, string[] directories, bool create)
    {
        for (var i = 1; i <= directories.Length; i++)
        {
            var path = Join(directories.Take(i));
            var status = store.CreateFile(out var handle, out _, path, ReadAttributes | AccessMask.SYNCHRONIZE | (create ? AddSubdirectory : 0), SmbAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, create ? CreateDisposition.FILE_OPEN_IF : CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_OPEN_REPARSE_POINT, null);
            if (!create && status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NOT_A_DIRECTORY) return false;
            Check(status);
            try
            {
                Check(store.GetFileInformation(out var result, handle, FileInformationClass.FileBasicInformation));
                if (((FileBasicInformation)result).FileAttributes.HasFlag(SmbAttributes.ReparsePoint))
                    throw new StorageException(StorageFailureKind.AccessDenied, "The storage location contains a link, which is never followed.");
            }
            finally { store.CloseFile(handle); }
        }
        return true;
    }

    private readonly record struct FileStat(long Length, DateTime Modified, bool IsDirectory);

    private FileStat? Stat(INTFileStore store, string[] segments)
    {
        if (!EnsureDirectories(store, segments[..^1], create: false)) return null;
        return Probe(store, Join(segments));
    }

    private static FileStat? Probe(INTFileStore store, string path)
    {
        var status = store.CreateFile(out var handle, out _, path, ReadAttributes | AccessMask.SYNCHRONIZE, SmbAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_OPEN_REPARSE_POINT, null);
        if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND) return null;
        Check(status);
        try
        {
            Check(store.GetFileInformation(out var result, handle, FileInformationClass.FileNetworkOpenInformation));
            var info = (FileNetworkOpenInformation)result;
            if (info.FileAttributes.HasFlag(SmbAttributes.ReparsePoint))
                throw new StorageException(StorageFailureKind.AccessDenied, "The storage location contains a link, which is never followed.");
            return new FileStat(info.EndOfFile, info.LastWriteTime ?? DateTime.UtcNow, info.FileAttributes.HasFlag(SmbAttributes.Directory));
        }
        finally { store.CloseFile(handle); }
    }

    private static bool IsDirectory(INTFileStore store, string path) => Probe(store, path) is { IsDirectory: true };

    private static SmbReadStream OpenRead(SmbSession session, string[] segments, long length, bool ownsSession)
    {
        var status = session.Store.CreateFile(out var handle, out _, Join(segments), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SmbAttributes.Normal,
            ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SEQUENTIAL_ONLY | CreateOptions.FILE_OPEN_REPARSE_POINT, null);
        if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
            throw new StorageException(StorageFailureKind.NotFound, "The stored object does not exist.");
        Check(status);
        return new SmbReadStream(session, handle, length, ownsSession);
    }

    private static async Task<(long Length, ContentChecksum Checksum)> WriteAsync(SmbSession session, object handle, Stream content, StoragePutOptions put, IProgress<long>? progress, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = (int)Math.Min(BlockSize, Math.Max(4096, session.MaxWriteSize));
        var buffer = ArrayPool<byte>.Shared.Rent(block);
        long total = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read;
                try { read = await content.ReadAsync(buffer.AsMemory(0, block), token).ConfigureAwait(false); }
                catch (IOException exception) { throw new StorageException(StorageFailureKind.Io, "The content to store could not be read.", exception); }
                if (read == 0) break;
                if (put.ExpectedLength is { } bound && total + read > bound) throw LengthMismatch();
                hash.AppendData(buffer, 0, read);
                var chunk = buffer.AsSpan(0, read).ToArray();
                var offset = 0;
                while (offset < chunk.Length)
                {
                    Check(session.Store.WriteFile(out var written, handle, total + offset, offset == 0 ? chunk : chunk[offset..]));
                    if (written <= 0) throw new StorageException(StorageFailureKind.Io, "The SMB share accepted no data.");
                    offset += written;
                }
                total += read;
                progress?.Report(total);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (put.ExpectedLength is { } expected && total != expected) throw LengthMismatch();
        var checksum = ContentChecksum.Sha256(hash.GetHashAndReset());
        if (put.ExpectedChecksum is { } wanted && wanted != checksum)
            throw new StorageException(StorageFailureKind.IntegrityMismatch, "The content checksum differs from the expected checksum.");
        return (total, checksum);
    }

    private static StorageException LengthMismatch() => new(StorageFailureKind.IntegrityMismatch, "The content length differs from the expected length.");

    internal static void Check(NTStatus status)
    {
        if (status == NTStatus.STATUS_SUCCESS) return;
        throw Map(status);
    }

    internal static StorageException Map(NTStatus status) => status switch
    {
        NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_WRONG_PASSWORD or NTStatus.STATUS_ACCOUNT_DISABLED or NTStatus.STATUS_ACCOUNT_LOCKED_OUT
            or NTStatus.STATUS_ACCOUNT_EXPIRED or NTStatus.STATUS_PASSWORD_EXPIRED or NTStatus.STATUS_ACCOUNT_RESTRICTION
            or NTStatus.STATUS_INVALID_LOGON_HOURS or NTStatus.STATUS_LOGON_TYPE_NOT_GRANTED or NTStatus.STATUS_PASSWORD_MUST_CHANGE
            => new(StorageFailureKind.AuthenticationFailed, "The SMB credentials were rejected."),
        NTStatus.STATUS_BAD_NETWORK_NAME => new(StorageFailureKind.InvalidConfiguration, "The SMB share was not found on the server."),
        NTStatus.STATUS_ACCESS_DENIED or NTStatus.STATUS_SHARING_VIOLATION => new(StorageFailureKind.AccessDenied, "The SMB share refused access."),
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE => new(StorageFailureKind.NotFound, "The stored object does not exist."),
        NTStatus.STATUS_OBJECT_NAME_COLLISION => AlreadyExists(),
        NTStatus.STATUS_DISK_FULL => new(StorageFailureKind.InsufficientSpace, "The SMB share has no space left."),
        NTStatus.STATUS_IO_TIMEOUT or NTStatus.STATUS_USER_SESSION_DELETED => new(StorageFailureKind.NetworkUnavailable, "The SMB connection was lost."),
        _ => new(StorageFailureKind.Io, "The SMB share could not be read or written.")
    };

    /// <summary>An authenticated session on one share; disposing it logs off and closes the socket.</summary>
    internal sealed class SmbSession : IDisposable
    {
        private readonly SMB2Client _client;
        private SmbSession(SMB2Client client, ISMBFileStore store) { _client = client; Store = store; }
        public ISMBFileStore Store { get; }
        public uint MaxWriteSize => _client.MaxWriteSize;
        public uint MaxReadSize => _client.MaxReadSize;

        public static async Task<SmbSession> OpenAsync(SmbStorageOptions options, SmbCredential credential, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var address = await ResolveAsync(options.Host, token).ConfigureAwait(false);
            var client = new SMB2Client(enableSMB311Support: true);
            try
            {
                var connect = Task.Run(() => client.Connect(address, SMBTransportType.DirectTCPTransport), CancellationToken.None);
                if (await Task.WhenAny(connect, Task.Delay(ConnectTimeout, token)).ConfigureAwait(false) != connect)
                {
                    token.ThrowIfCancellationRequested();
                    throw new StorageException(StorageFailureKind.Timeout, "The SMB server did not answer in time.");
                }
                if (!await connect.ConfigureAwait(false)) throw new StorageException(StorageFailureKind.NetworkUnavailable, "The SMB server could not be reached or negotiated a supported protocol (SMB 2.1 or newer).");
                Check(client.Login(credential.Domain ?? string.Empty, credential.Username, credential.Password, AuthenticationMethod.NTLMv2));
                var store = client.TreeConnect(options.Share, out var status);
                Check(status);
                return new SmbSession(client, store);
            }
            catch { try { client.Disconnect(); } catch (Exception) { } throw; }
        }

        private static async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
        {
            if (IPAddress.TryParse(host, out var literal)) return literal;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                return addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.First();
            }
            catch (System.Net.Sockets.SocketException exception) { throw new StorageException(StorageFailureKind.NetworkUnavailable, "The SMB host name could not be resolved.", exception); }
        }

        public void Dispose()
        {
            try { Store.Disconnect(); _client.Logoff(); } catch (Exception) { /* the socket is closed next either way */ }
            try { _client.Disconnect(); } catch (Exception) { }
        }
    }

    /// <summary>Sequential read stream over an open SMB handle; the session stays open until the stream is disposed.</summary>
    private sealed class SmbReadStream : Stream
    {
        private readonly SmbSession _session;
        private readonly object _handle;
        private readonly long _length;
        private readonly bool _ownsSession;
        private long _position;
        private byte[] _buffer = [];
        private int _bufferOffset;

        public SmbReadStream(SmbSession session, object handle, long length, bool ownsSession) { _session = session; _handle = handle; _length = length; _ownsSession = ownsSession; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position - (_buffer.Length - _bufferOffset); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_bufferOffset == _buffer.Length)
            {
                if (_position >= _length) return 0;
                // One network round trip per block, however small the caller's reads are.
                var want = (int)Math.Min(Math.Min(BlockSize, _length - _position), Math.Max(4096u, _session.MaxReadSize));
                Check(_session.Store.ReadFile(out var data, _handle, _position, want));
                if (data.Length == 0) return 0;
                _buffer = data; _bufferOffset = 0; _position += data.Length;
            }
            var count = Math.Min(buffer.Length, _buffer.Length - _bufferOffset);
            _buffer.AsSpan(_bufferOffset, count).CopyTo(buffer);
            _bufferOffset += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            token.IsCancellationRequested ? ValueTask.FromCanceled<int>(token) : ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _session.Store.CloseFile(_handle); } catch (Exception) { }
                if (_ownsSession) _session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
