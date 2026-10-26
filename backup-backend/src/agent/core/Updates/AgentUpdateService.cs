using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Encryption;

namespace BackupPlatform.Agent.Core.Updates;

/// <summary>Verifies pinned metadata before downloading, then extracts only verified bounded packages.</summary>
public sealed class AgentUpdateService
{
    public const long MaximumPackageBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const int MaximumEntries = 8192;

    public static Version ValidateManifest(AgentUpdateManifest manifest, string platform, IReadOnlyDictionary<string, string> trustedKeys)
    {
        if (manifest?.Payload is not { } payload || platform is not ("linux-x64" or "linux-arm64" or "win-x64")
            || payload.Platform != platform || !CanonicalVersion(payload.Version, out var target)
            || !CanonicalVersion(payload.MinimumSupportedVersion, out var minimum) || minimum > target
            || payload.PackageSize is <= 0 or > MaximumPackageBytes || payload.PackageSha256?.Length != 64
            || !payload.PackageSha256.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(manifest.KeyId)
            || !trustedKeys.TryGetValue(manifest.KeyId, out var pem)) throw new InvalidDataException("Invalid or untrusted update manifest.");
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        if (!AgentUpdateManifestVerifier.Verify(manifest, key.ExportSubjectPublicKeyInfo())) throw new InvalidDataException("Invalid update signature.");
        return target;
    }

    public static bool CanonicalVersion(string? value, out Version version)
    {
        if (Version.TryParse(value, out var parsed) && parsed.ToString() == value)
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    /// <summary>Copies with constant memory; excess data, truncation and digest mismatches fail before extraction.</summary>
    public async Task DownloadAsync(Stream source, string destination, AgentUpdateManifest manifest, string platform,
        IReadOnlyDictionary<string, string> trustedKeys, CancellationToken cancellationToken)
    {
        ValidateManifest(manifest, platform, trustedKeys);
        var created = false;
        try
        {
            await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long size = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                size += count;
                if (size > manifest.Payload.PackageSize) throw new InvalidDataException("Update package exceeds its signed size.");
                hash.AppendData(buffer, 0, count);
                await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (size != manifest.Payload.PackageSize || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), manifest.Payload.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update package integrity mismatch.");
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            file.Flush(true);
        }
        catch { if (created) File.Delete(destination); throw; }
    }

    /// <summary>Revalidates the file handle used for extraction and atomically publishes a new version directory.</summary>
    public async Task<string> StageAsync(string package, string releases, AgentUpdateManifest manifest, string platform,
        IReadOnlyDictionary<string, string> trustedKeys, CancellationToken cancellationToken)
    {
        ValidateManifest(manifest, platform, trustedKeys);
        AssertNoLinks(releases);
        Directory.CreateDirectory(releases);
        var target = Path.Combine(releases, manifest.Payload.Version);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Release already exists.");
        var stage = Path.Combine(releases, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            await using var file = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (file.Length != manifest.Payload.PackageSize || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)), manifest.Payload.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update package integrity mismatch.");
            file.Position = 0;
            await ExtractAsync(file, stage, platform, cancellationToken).ConfigureAwait(false);
            var executable = Path.Combine(stage, platform == "win-x64" ? "BackupPlatform.Agent.Host.exe" : "BackupPlatform.Agent.Host");
            if (!File.Exists(executable)) throw new InvalidDataException("The release has no agent executable.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Directory.Move(stage, target);
            return target;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static async Task ExtractAsync(Stream package, string stage, string platform, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        string Destination(string name, bool directory, int mode)
        {
            if (names.Count >= MaximumEntries || string.IsNullOrEmpty(name) || name.Contains('\\') || name.StartsWith('/') || name.Contains(':')
                || (mode & 0xF000) is not (0 or 0x8000 or 0x4000) || (mode & 0xE12) != 0)
                throw new InvalidDataException("Unsafe archive entry.");
            var normalized = name.TrimEnd('/');
            var parts = normalized.Split('/');
            if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>\"|?*".Contains(c))
                || IsReserved(part)) || !names.Add(normalized)) throw new InvalidDataException("Unsafe or duplicate archive path.");
            var leaf = parts[^1];
            if (leaf.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".env", StringComparison.OrdinalIgnoreCase)
                || leaf.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
                || parts.Any(part => part.Equals("identity", StringComparison.OrdinalIgnoreCase) || part.Equals("config", StringComparison.OrdinalIgnoreCase))
                || leaf.StartsWith("agent-identity", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release packages cannot contain configuration or identity.");
            var destination = Path.Combine(stage, Path.Combine(parts));
            if (directory) Directory.CreateDirectory(destination);
            else Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            return destination;
        }
        async Task Copy(Stream source, string destination, long length)
        {
            if (length < 0 || length > MaximumExpandedBytes - expanded) throw new InvalidDataException("Expanded update exceeds its limit.");
            expanded += length;
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            var buffer = new byte[65536];
            long actual = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                actual += count;
                if (actual > length) throw new InvalidDataException("Expanded entry exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (actual != length) throw new InvalidDataException("Truncated archive entry.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        if (platform == "win-x64")
        {
            await ValidateZipDirectoryAsync(package, token).ConfigureAwait(false);
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > MaximumEntries) throw new InvalidDataException("Too many update entries.");
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var directory = entry.FullName.EndsWith('/');
                var destination = Destination(entry.FullName, directory, (entry.ExternalAttributes >> 16) & 0xFFFF);
                if (directory) { if (entry.Length != 0) throw new InvalidDataException("Directory entry has content."); continue; }
                await using var content = entry.Open();
                await Copy(content, destination, entry.Length).ConfigureAwait(false);
            }
        }
        else
        {
            using var gzip = new GZipStream(package, CompressionMode.Decompress, true);
            using var bounded = new BoundedArchiveStream(gzip, MaximumExpandedBytes + MaximumEntries * 4096L);
            using var tar = new TarReader(bounded, true);
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(false, token).ConfigureAwait(false)) is not null)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) throw new InvalidDataException("Links and special archive entries are forbidden.");
                var directory = entry.EntryType == TarEntryType.Directory;
                var destination = Destination(entry.Name, directory, (int)entry.Mode);
                if (!directory) await Copy(entry.DataStream ?? Stream.Null, destination, entry.Length).ConfigureAwait(false);
            }
            var drain = new byte[65536];
            while (await bounded.ReadAsync(drain, token).ConfigureAwait(false) != 0) { }
        }
    }

    private static async Task ValidateZipDirectoryAsync(Stream package, CancellationToken token)
    {
        var tail = new byte[(int)Math.Min(package.Length, 65557)];
        package.Position = package.Length - tail.Length;
        await package.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == 0x06054b50
                && index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2)) == tail.Length) { end = index; break; }
        if (end < 0) throw new InvalidDataException("Missing ZIP directory.");
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10, 2));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12, 4));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16, 4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 4, 4)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8, 2)) != entries || entries > MaximumEntries
            || size > 8 * 1024 * 1024 || (long)offset + size > package.Length - tail.Length + end)
            throw new InvalidDataException("Oversized, ZIP64 or multi-volume ZIP directory.");
        package.Position = offset;
        var header = new byte[46];
        long expanded = 0;
        for (var index = 0; index < entries; index++)
        {
            await package.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50) throw new InvalidDataException("Invalid ZIP directory entry.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4));
            var name = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
            var comment = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            if (length > MaximumExpandedBytes - expanded || name > 1024 || extra > 4096 || comment > 1024
                || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34, 2)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42, 4)) >= offset)
                throw new InvalidDataException("Oversized or invalid ZIP entry.");
            expanded += length;
            package.Position += name + extra + comment;
            if (package.Position > (long)offset + size) throw new InvalidDataException("Truncated ZIP directory.");
        }
        if (package.Position != (long)offset + size) throw new InvalidDataException("ZIP directory count mismatch.");
        package.Position = 0;
    }

    private static bool IsReserved(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9');
    }

    public static void AssertNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths cannot contain links.");
    }

    private sealed class BoundedArchiveStream(Stream inner, long limit) : Stream
    {
        private readonly byte[] _header = new byte[512];
        private long _read;
        private long _remaining;
        private long _expanded;
        private int _position = 512;
        private int _entries;
        private int _zeroHeaders;

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            if (_position == 512 && _remaining == 0)
            {
                var count = 0;
                while (count < 512)
                {
                    var read = await ReadInnerAsync(_header.AsMemory(count), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (count != 0 || _zeroHeaders < 2) throw new InvalidDataException("Truncated tar archive.");
                        return 0;
                    }
                    count += read;
                }
                ValidateHeader();
                _position = 0;
            }
            if (_position < 512)
            {
                var count = Math.Min(buffer.Length, 512 - _position);
                _header.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            var copied = await ReadInnerAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], token).ConfigureAwait(false);
            if (copied == 0) throw new InvalidDataException("Truncated tar entry.");
            _remaining -= copied;
            return copied;
        }

        private async ValueTask<int> ReadInnerAsync(Memory<byte> buffer, CancellationToken token)
        {
            var count = await inner.ReadAsync(buffer, token).ConfigureAwait(false);
            _read += count;
            if (_read > limit) throw new InvalidDataException("Decompressed archive exceeds its limit.");
            return count;
        }

        private void ValidateHeader()
        {
            if (_header.All(value => value == 0)) { _zeroHeaders++; return; }
            // Reject extension headers before TarReader can allocate their attacker-declared metadata.
            if (_zeroHeaders != 0 || ++_entries > MaximumEntries || _header[156] is not (0 or (byte)'0' or (byte)'5'))
                throw new InvalidDataException("Only regular ustar files and directories are allowed.");
            long size = 0;
            foreach (var digit in _header.AsSpan(124, 12))
            {
                if (digit is 0 or (byte)' ') continue;
                if (digit is < (byte)'0' or > (byte)'7') throw new InvalidDataException("Invalid tar entry size.");
                size = size * 8 + digit - '0';
                if (size > MaximumExpandedBytes) throw new InvalidDataException("Expanded tar entry exceeds its limit.");
            }
            if ((_header[156] == (byte)'5' && size != 0) || size > MaximumExpandedBytes - _expanded)
                throw new InvalidDataException("Expanded tar archive exceeds its limit.");
            _expanded += size;
            _remaining = (size + 511) / 512 * 512;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
