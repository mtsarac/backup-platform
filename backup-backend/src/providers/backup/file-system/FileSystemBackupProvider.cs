using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BackupPlatform.BackupProviders.Abstractions;
using BackupPlatform.SnapshotProviders.Abstractions;

namespace BackupPlatform.BackupProviders.FileSystem;

public enum FileSymlinkPolicy { Skip, Preserve }

public sealed record FileSystemBackupSource(string RootDirectory, IReadOnlyList<string> Includes, IReadOnlyList<string> Excludes,
    FileSymlinkPolicy SymlinkPolicy = FileSymlinkPolicy.Skip, IReadOnlyList<string>? SelectedPaths = null,
    SnapshotKind? SnapshotKind = null, string? SnapshotSource = null, string SnapshotRelativePath = ".", string? LvmSize = null,
    string SourceReference = "default", bool Compress = true);

public sealed record FileManifestEntry(string RelativePath, long SizeBytes, string Sha256Checksum, DateTimeOffset LastWriteTimeUtc,
    UnixFileMode? UnixMode = null, bool IsSymlink = false, string? SymlinkTarget = null, bool IsDirectory = false,
    string? WindowsAccessControl = null);

public sealed record FileBackupManifest(int Version, DateTimeOffset CreatedAt, string SourceReference, IReadOnlyList<FileManifestEntry> Files);

/// <summary>Creates streaming, manifest-verified archives; restoration validates all selected bytes before replacing any target file.</summary>
public sealed class FileSystemBackupProvider(IEnumerable<ISnapshotProvider> snapshots, IWindowsFilePermissions? windowsPermissions)
{
    private const string ManifestName = "manifest.json";
    private const string ContentPrefix = "content/";
    private const int MaximumManifestBytes = 64 * 1024 * 1024;
    private static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private const UnixFileMode SafeModes = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public async Task<(FileBackupManifest Manifest, string StagedArchivePath)> CreateBackupAsync(FileSystemBackupSource source,
        string stagingDirectory, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequirePlatformPermissions();
        var root = ExistingRoot(source.RootDirectory);
        var staging = ExistingRoot(stagingDirectory);
        if (IsWithin(root, staging) || Paths.Equals(root, staging)) throw new ArgumentException("Staging must be outside the backup source.");
        if (!Enum.IsDefined(source.SymlinkPolicy)) throw new ArgumentException("Unknown symbolic link policy.");
        var includes = CompileGlobs(source.Includes.Count == 0 ? ["**"] : source.Includes);
        var excludes = CompileGlobs(source.Excludes);
        var selected = (source.SelectedPaths is { Count: > 0 } ? source.SelectedPaths : ["."]).Select(path => Normalize(path, allowRoot: true)).Distinct(Paths).ToArray();
        foreach (var path in selected) _ = CheckedPath(root, path, allowFinalLink: true);
        SnapshotLease? lease = null;
        var archivePath = Path.Combine(staging, "files_" + Guid.NewGuid().ToString("N") + ".zip");
        var succeeded = false;
        try
        {
            if (source.SnapshotKind is { } kind)
            {
                var provider = snapshots.SingleOrDefault(candidate => candidate.Kind == kind) ?? throw new InvalidOperationException("The configured snapshot provider is unavailable.");
                var nativeSource = source.SnapshotSource ?? (kind == SnapshotKind.Vss ? Path.GetPathRoot(root)! : root);
                var relative = kind == SnapshotKind.Vss ? Path.GetRelativePath(nativeSource, root) : source.SnapshotRelativePath;
                lease = await provider.CreateAsync(new SnapshotRequest(nativeSource, staging, "bp_" + Guid.NewGuid().ToString("N"), source.LvmSize), cancellationToken).ConfigureAwait(false);
                root = CheckedPath(lease.Path, Normalize(relative, allowRoot: true));
            }
            var entries = new List<FileManifestEntry>();
            long transferred = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(archivePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var queue = new Queue<string>();
                queue.Enqueue("");
                while (queue.TryDequeue(out var directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var item in new DirectoryInfo(CheckedPath(root, directory)).EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = directory.Length == 0 ? item.Name : directory + "/" + item.Name;
                        _ = Normalize(relative);
                        var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                        if (Excluded(relative, excludes) || !selected.Any(path => Covers(path, relative) || isDirectory && Covers(relative, path))) continue;
                        var isLink = (item.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (isLink)
                        {
                            if (source.SymlinkPolicy == FileSymlinkPolicy.Skip || !Included(relative, includes)) continue;
                            var target = item.LinkTarget ?? throw new IOException("An unsupported reparse point was encountered.");
                            ValidateLink(root, relative, target);
                            entries.Add(new(relative, 0, "", item.LastWriteTimeUtc, null, true, target, isDirectory));
                            continue;
                        }
                        var path = CheckedPath(root, relative);
                        if (isDirectory)
                        {
                            queue.Enqueue(relative);
                            entries.Add(new(relative, 0, "", item.LastWriteTimeUtc, Mode(path), IsDirectory: true,
                                WindowsAccessControl: windowsPermissions?.CaptureAccessControl(path, isDirectory: true)));
                            continue;
                        }
                        if (!Included(relative, includes) || !selected.Any(selection => Covers(selection, relative))) continue;
                        var before = new FileInfo(path);
                        var length = before.Length;
                        var modified = before.LastWriteTimeUtc;
                        var entry = archive.CreateEntry(ContentPrefix + relative, source.Compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var compressed = entry.Open();
                        long copied = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            await compressed.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                            hash.AppendData(buffer, 0, count);
                            copied += count;
                            transferred += count;
                            progress?.Report(transferred);
                        }
                        before.Refresh();
                        if (copied != length || before.Length != length || before.LastWriteTimeUtc != modified)
                            throw new IOException("A source file changed while it was being backed up; use a snapshot for a consistent view.");
                        entries.Add(new(relative, copied, Convert.ToHexStringLower(hash.GetHashAndReset()), modified, Mode(path),
                            WindowsAccessControl: windowsPermissions?.CaptureAccessControl(path, isDirectory: false)));
                    }
                }
                foreach (var selection in selected)
                    if (selection.Length != 0 && !entries.Any(entry => Covers(selection, entry.RelativePath)))
                        throw new FileNotFoundException("A selected source does not exist or matches no included entries.");
                var manifest = new FileBackupManifest(1, DateTimeOffset.UtcNow, source.SourceReference, entries);
                await using (var stream = archive.CreateEntry(ManifestName, CompressionLevel.Optimal).Open())
                    await JsonSerializer.SerializeAsync(stream, manifest, FileBackupJsonContext.Default.FileBackupManifest, cancellationToken).ConfigureAwait(false);
                await archive.DisposeAsync().ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                succeeded = true;
                return (manifest, archivePath);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            if (!succeeded && File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    public async Task<FileBackupManifest> InspectAsync(string archivePath, CancellationToken cancellationToken, bool verifyContents = true)
    {
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = await ReadManifestAsync(archive, cancellationToken).ConfigureAwait(false);
        if (verifyContents)
        {
            foreach (var entry in manifest.Files.Where(entry => !entry.IsDirectory && !entry.IsSymlink))
            {
                await using var content = archive.GetEntry(ContentPrefix + entry.RelativePath)!.Open();
                var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(checksum, entry.Sha256Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An archive entry does not match its manifest checksum.");
            }
        }
        return manifest;
    }

    public async Task RestoreAsync(string stagedArchivePath, string targetDirectory, bool overwrite, IReadOnlyList<string>? selectedFiles,
        IProgress<long>? progress, CancellationToken cancellationToken, string relativeDirectory = "")
    {
        RequirePlatformPermissions();
        var root = ExistingRoot(targetDirectory);
        root = CheckedPath(root, Normalize(relativeDirectory, allowRoot: true));
        if (!Directory.Exists(root)) CreatePrivateDirectory(root);
        if (selectedFiles is { Count: 0 }) throw new ArgumentException("An explicit restore selection cannot be empty.", nameof(selectedFiles));
        await using var input = new FileStream(stagedArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = await ReadManifestAsync(archive, cancellationToken).ConfigureAwait(false);
        var selection = selectedFiles?.Select(path => Normalize(path, allowRoot: true)).Distinct(Paths).ToArray();
        var entries = manifest.Files.Where(entry => selection is null || selection.Any(path => Covers(path, entry.RelativePath))).ToArray();
        if (selection is not null && selection.Any(path => !entries.Any(entry => Covers(path, entry.RelativePath))))
            throw new ArgumentException("A restore selection is not present in the manifest.", nameof(selectedFiles));
        foreach (var entry in entries)
        {
            if (entry.WindowsAccessControl is { } descriptor)
            {
                if (windowsPermissions is null)
                    throw new PlatformNotSupportedException("Restoring Windows access restrictions requires the Windows platform adapter.");
                windowsPermissions.ValidateAccessControl(descriptor);
            }
            var target = CheckedPath(root, entry.RelativePath);
            if (entry.IsSymlink) ValidateLink(root, entry.RelativePath, entry.SymlinkTarget!);
            if (!entry.IsDirectory && Directory.Exists(target)) throw new IOException("A restore file conflicts with a directory.");
            if (File.Exists(target) && (entry.IsDirectory || !overwrite)) throw new IOException("An existing restore target cannot be overwritten.");
        }
        var staging = Path.Combine(root, ".bp-restore-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(staging);
        var applied = new List<(string Target, string? Backup, bool Installed)>();
        var createdDirectories = new List<string>();
        var removeStaging = false;
        try
        {
            long transferred = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var index = 0;
                foreach (var entry in entries.Where(entry => !entry.IsDirectory && !entry.IsSymlink))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var zipEntry = archive.GetEntry(ContentPrefix + entry.RelativePath)!;
                    var staged = Path.Combine(staging, "file-" + index++);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using var from = zipEntry.Open();
                    await using var to = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                    long copied = 0;
                    int count;
                    while ((count = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        copied = checked(copied + count);
                        if (copied > entry.SizeBytes) throw new InvalidDataException("An archive entry exceeds its manifest size.");
                        hash.AppendData(buffer, 0, count);
                        await to.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        transferred += count;
                        progress?.Report(transferred);
                    }
                    if (copied != entry.SizeBytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), entry.Sha256Checksum, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("An archive entry does not match its manifest checksum.");
                    await to.FlushAsync(cancellationToken).ConfigureAwait(false);
                    to.Flush(flushToDisk: true);
                    await to.DisposeAsync().ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(staged, entry.LastWriteTimeUtc.UtcDateTime);
                    ApplyPermissions(staged, entry);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            foreach (var entry in entries.Where(entry => entry.IsDirectory && !entry.IsSymlink).OrderBy(entry => entry.RelativePath.Length))
                EnsureDirectories(root, entry.RelativePath, createdDirectories);
            var fileIndex = 0;
            foreach (var entry in entries.Where(entry => !entry.IsDirectory && !entry.IsSymlink).Concat(entries.Where(entry => entry.IsSymlink)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slash = entry.RelativePath.LastIndexOf('/');
                if (slash >= 0) EnsureDirectories(root, entry.RelativePath[..slash], createdDirectories);
                var target = CheckedPath(root, entry.RelativePath);
                string? backup = null;
                if (File.Exists(target))
                {
                    if (!overwrite) throw new IOException("A restore target was created concurrently.");
                    backup = Path.Combine(staging, "old-" + applied.Count);
                    File.Move(target, backup);
                }
                applied.Add((target, backup, false));
                if (entry.IsSymlink)
                {
                    ValidateLink(root, entry.RelativePath, entry.SymlinkTarget!);
                    if (entry.IsDirectory) Directory.CreateSymbolicLink(target, entry.SymlinkTarget!);
                    else File.CreateSymbolicLink(target, entry.SymlinkTarget!);
                }
                else File.Move(Path.Combine(staging, "file-" + fileIndex++), target);
                applied[^1] = (target, backup, true);
            }
            foreach (var entry in entries.Where(entry => entry.IsDirectory && !entry.IsSymlink).OrderByDescending(entry => entry.RelativePath.Length))
            {
                var target = CheckedPath(root, entry.RelativePath);
                if (!createdDirectories.Contains(target, Paths)) continue;
                Directory.SetLastWriteTimeUtc(target, entry.LastWriteTimeUtc.UtcDateTime);
                ApplyPermissions(target, entry);
            }
            removeStaging = true;
        }
        catch (Exception original)
        {
            var errors = new List<Exception> { original };
            foreach (var directory in createdDirectories)
                try
                {
                    if (windowsPermissions is not null) windowsPermissions.CreatePrivateDirectory(directory);
                    else SetMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (Exception error) { errors.Add(error); }
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.Installed)
                    {
                        if ((File.GetAttributes(item.Target) & FileAttributes.Directory) != 0) Directory.Delete(item.Target);
                        else File.Delete(item.Target);
                    }
                    if (item.Backup is not null) File.Move(item.Backup, item.Target);
                }
                catch (Exception error) { errors.Add(error); }
            }
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                try { Directory.Delete(directory); } catch (Exception error) { errors.Add(error); }
            removeStaging = errors.Count == 1;
            if (!removeStaging) throw new AggregateException("Restore failed and rollback is incomplete; the owned staging directory retains original files.", errors);
            throw;
        }
        finally { if (removeStaging) Directory.Delete(staging, recursive: true); }
    }

    private static async Task<FileBackupManifest> ReadManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(Paths);
        foreach (var entry in archive.Entries)
        {
            _ = Normalize(entry.FullName);
            if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate archive entries are not allowed.");
        }
        var manifestEntry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("The file manifest is missing.");
        if (manifestEntry.Length > MaximumManifestBytes) throw new InvalidDataException("The file manifest is too large.");
        await using var stream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync(stream, FileBackupJsonContext.Default.FileBackupManifest, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The file manifest is invalid.");
        if (manifest.Version != 1 || manifest.Files is null) throw new InvalidDataException("The file manifest version is unsupported.");
        var files = new Dictionary<string, FileManifestEntry>(Paths);
        foreach (var entry in manifest.Files)
        {
            _ = Normalize(entry.RelativePath);
            if (!files.TryAdd(entry.RelativePath, entry) || entry.SizeBytes < 0 || entry.UnixMode is { } mode && (mode & ~SafeModes) != 0)
                throw new InvalidDataException("The file manifest contains invalid or duplicate entries.");
            if (entry.IsSymlink && string.IsNullOrEmpty(entry.SymlinkTarget)) throw new InvalidDataException("A symbolic link target is missing.");
            if (entry.WindowsAccessControl is { } descriptor
                && (entry.IsSymlink || descriptor.Length is 0 or > 65536 || entry.UnixMode is not null))
                throw new InvalidDataException("The file manifest contains invalid Windows access metadata.");
            if (!entry.IsDirectory && !entry.IsSymlink)
            {
                if (entry.Sha256Checksum is not { Length: 64 } || !entry.Sha256Checksum.All(char.IsAsciiHexDigit)) throw new InvalidDataException("A file checksum is invalid.");
                var content = archive.GetEntry(ContentPrefix + entry.RelativePath);
                if (content is null || content.Length != entry.SizeBytes) throw new InvalidDataException("Archive content disagrees with the manifest.");
            }
        }
        foreach (var entry in files.Values)
        {
            var parent = entry.RelativePath;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            {
                parent = parent[..slash];
                if (files.TryGetValue(parent, out var ancestor) && (!ancestor.IsDirectory || ancestor.IsSymlink))
                    throw new InvalidDataException("An archive entry is nested below a file or link.");
            }
        }
        if (archive.Entries.Count != files.Values.Count(entry => !entry.IsDirectory && !entry.IsSymlink) + 1)
            throw new InvalidDataException("The archive contains content not declared in its manifest.");
        return manifest;
    }

    private static Regex[] CompileGlobs(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return patterns.Select(pattern =>
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 1024 || pattern.Contains('\\') || pattern.StartsWith('/') || pattern.Split('/').Any(segment => segment == ".."))
                throw new ArgumentException("Filters must be relative forward-slash wildcard patterns.");
            var expression = new StringBuilder("^");
            for (var index = 0; index < pattern.Length; index++)
            {
                var character = pattern[index];
                if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/') { index++; expression.Append("(?:.*/)?"); }
                    else expression.Append(".*");
                }
                else if (character == '*') expression.Append("[^/]*");
                else if (character == '?') expression.Append("[^/]");
                else expression.Append(Regex.Escape(character.ToString()));
            }
            expression.Append('$');
            return new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
                | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
        }).ToArray();
    }

    private static bool Included(string path, Regex[] patterns) => patterns.Any(pattern => pattern.IsMatch(path));
    private static bool Excluded(string path, Regex[] patterns)
    {
        while (true)
        {
            if (patterns.Any(pattern => pattern.IsMatch(path) || pattern.IsMatch(path + "/"))) return true;
            var slash = path.LastIndexOf('/');
            if (slash < 0) return false;
            path = path[..slash];
        }
    }

    private static bool Covers(string selection, string path) => selection.Length == 0 || Paths.Equals(selection, path) || path.StartsWith(selection + "/", PathComparison);
    private static string Normalize(string path, bool allowRoot = false)
    {
        if (allowRoot && path is "" or ".") return "";
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) || Path.IsPathRooted(path)
            || path.Split('/').Any(segment => segment is "" or "." or "..")) throw new InvalidDataException("A confined relative path is required.");
        return path;
    }

    private static string ExistingRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("An existing absolute non-link directory is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);
    private static string CheckedPath(string root, string relative, bool allowFinalLink = false)
    {
        var result = root;
        if (relative.Length == 0) return result;
        var parts = Normalize(relative).Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            result = Path.Combine(result, parts[index]);
            try
            {
                if ((File.GetAttributes(result) & FileAttributes.ReparsePoint) != 0 && !(allowFinalLink && index == parts.Length - 1))
                    throw new IOException("A path contains a symbolic link or reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    private static void ValidateLink(string root, string relative, string target)
    {
        if (string.IsNullOrEmpty(target) || Path.IsPathRooted(target) || target.Contains('\\') || target.Contains(':') || target.Any(char.IsControl))
            throw new InvalidDataException("Only confined relative symbolic links can be preserved.");
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.Combine(root, relative))!, target));
        if (!IsWithin(root, resolved) && !Paths.Equals(root, resolved)) throw new InvalidDataException("A symbolic link escapes the source or target root.");
        _ = CheckedPath(root, Path.GetRelativePath(root, resolved).Replace('\\', '/'));
    }

    private void RequirePlatformPermissions()
    {
        if (OperatingSystem.IsWindows() && windowsPermissions is null)
            throw new InvalidOperationException("Windows file backups require the Windows permissions adapter.");
    }

    private void ApplyPermissions(string path, FileManifestEntry entry)
    {
        SetMode(path, entry.UnixMode);
        if (entry.WindowsAccessControl is { } descriptor)
            windowsPermissions!.ApplyAccessControl(path, entry.IsDirectory, descriptor);
    }

    private UnixFileMode? Mode(string path) => OperatingSystem.IsWindows() || windowsPermissions is not null ? null : File.GetUnixFileMode(path) & SafeModes;
    private static void SetMode(string path, UnixFileMode? mode)
    {
        if (!OperatingSystem.IsWindows() && mode is { } value) File.SetUnixFileMode(path, value & SafeModes);
    }
    private void CreatePrivateDirectory(string path)
    {
        if (windowsPermissions is not null) windowsPermissions.CreatePrivateDirectory(path);
        else if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else throw new InvalidOperationException("Windows file restores require the Windows permissions adapter.");
    }
    private void EnsureDirectories(string root, string relative, List<string> created)
    {
        var prefix = "";
        foreach (var part in Normalize(relative).Split('/'))
        {
            prefix = prefix.Length == 0 ? part : prefix + "/" + part;
            var path = CheckedPath(root, prefix);
            if (Directory.Exists(path)) continue;
            CreatePrivateDirectory(path);
            created.Add(path);
        }
    }
}

[JsonSerializable(typeof(FileBackupManifest))]
internal partial class FileBackupJsonContext : JsonSerializerContext;
