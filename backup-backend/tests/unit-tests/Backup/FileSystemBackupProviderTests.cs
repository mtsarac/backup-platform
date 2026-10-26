using System.IO.Compression;
using System.Text.Json;
using BackupPlatform.BackupProviders.Abstractions;
using BackupPlatform.BackupProviders.FileSystem;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class FileSystemBackupProviderTests
{
    [Fact]
    public async Task CreateBackup_and_Restore_roundtrips_files_preserves_hashes_and_excludes_patterns()
    {
        var temp = Path.Combine(Path.GetTempPath(), "bp-fs-test-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(temp, "source");
        var stagingDir = Path.Combine(temp, "staging");
        var restoreDir = Path.Combine(temp, "restore");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(restoreDir);

        try
        {
            var file1 = Path.Combine(sourceDir, "keep.txt");
            var file2 = Path.Combine(sourceDir, "ignored.log");
            var subDir = Path.Combine(sourceDir, "sub");
            Directory.CreateDirectory(subDir);
            var file3 = Path.Combine(subDir, "data.bin");

            await File.WriteAllTextAsync(file1, "keep this content");
            await File.WriteAllTextAsync(file2, "log content");
            await File.WriteAllBytesAsync(file3, new byte[] { 1, 2, 3, 4, 5 });

            var sut = new FileSystemBackupProvider([], OperatingSystem.IsWindows() ? new RecordingWindowsPermissions() : null);
            var source = new FileSystemBackupSource(
                sourceDir,
                Includes: ["**"],
                Excludes: ["*.log"]);

            var (manifest, archivePath) = await sut.CreateBackupAsync(source, stagingDir, null, CancellationToken.None);

            Assert.Contains(manifest.Files, f => f.RelativePath == "keep.txt");
            Assert.Contains(manifest.Files, f => f.RelativePath == "sub/data.bin");
            Assert.DoesNotContain(manifest.Files, f => f.RelativePath == "ignored.log");

            // Restore
            await sut.RestoreAsync(archivePath, restoreDir, overwrite: true, selectedFiles: null, null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(restoreDir, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(restoreDir, "sub", "data.bin")));
            Assert.False(File.Exists(Path.Combine(restoreDir, "ignored.log")));
            Assert.Equal("keep this content", await File.ReadAllTextAsync(Path.Combine(restoreDir, "keep.txt")));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(restoreDir, "sub", "data.bin")));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }
    [Fact]
    public async Task A_corrupt_selected_file_is_rejected_before_existing_targets_are_changed()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-corrupt-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "trusted data");
            await File.WriteAllTextAsync(Path.Combine(target, "data.txt"), "existing original");
            var provider = new FileSystemBackupProvider([], OperatingSystem.IsWindows() ? new RecordingWindowsPermissions() : null);
            var (_, archivePath) = await provider.CreateBackupAsync(new(source, ["**"], []), staging, null, default);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            {
                archive.GetEntry("content/data.txt")!.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("content/data.txt").Open());
                writer.Write("corrupt data");
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.RestoreAsync(archivePath, target, true, null, null, default));
            Assert.Equal("existing original", await File.ReadAllTextAsync(Path.Combine(target, "data.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Selected_directory_restore_does_not_restore_unselected_siblings()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-selection-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "selected"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "selected", "data.txt"), "selected content");
            await File.WriteAllTextAsync(Path.Combine(source, "other.txt"), "not selected");
            var provider = new FileSystemBackupProvider([], OperatingSystem.IsWindows() ? new RecordingWindowsPermissions() : null);
            var (_, archivePath) = await provider.CreateBackupAsync(new(source, ["**/*.txt"], []), staging, null, default);
            await provider.RestoreAsync(archivePath, target, false, ["selected"], null, default);
            Assert.Equal("selected content", await File.ReadAllTextAsync(Path.Combine(target, "selected", "data.txt")));
            Assert.False(File.Exists(Path.Combine(target, "other.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Windows_access_metadata_roundtrips_and_is_applied_before_files_are_installed()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-acl-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "private"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "private", "data.txt"), "restricted data");
            var permissions = new RecordingWindowsPermissions();
            var provider = new FileSystemBackupProvider([], permissions);
            var (manifest, archivePath) = await provider.CreateBackupAsync(new(source, ["**"], []), staging, null, default);
            Assert.All(manifest.Files, entry =>
            {
                Assert.Equal(RecordingWindowsPermissions.Descriptor, entry.WindowsAccessControl);
                Assert.Null(entry.UnixMode);
            });
            var inspected = await provider.InspectAsync(archivePath, default);
            Assert.Equal(manifest.Files.ToArray(), inspected.Files.ToArray());

            await provider.RestoreAsync(archivePath, target, false, null, null, default);

            Assert.Equal(2, permissions.Applied.Count);
            var file = Assert.Single(permissions.Applied, entry => !entry.Directory);
            Assert.Equal("file-0", Path.GetFileName(file.Path));
            Assert.StartsWith(target + Path.DirectorySeparatorChar + ".bp-restore-", file.Path);
            Assert.Equal(Path.Combine(target, "private"), Assert.Single(permissions.Applied, entry => entry.Directory).Path);
            Assert.Equal("restricted data", await File.ReadAllTextAsync(Path.Combine(target, "private", "data.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Invalid_windows_access_metadata_is_rejected_before_overwriting_existing_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-acl-invalid-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "replacement");
            await File.WriteAllTextAsync(Path.Combine(target, "data.txt"), "original");
            var permissions = new RecordingWindowsPermissions();
            var provider = new FileSystemBackupProvider([], permissions);
            var (_, archivePath) = await provider.CreateBackupAsync(new(source, ["**"], []), staging, null, default);
            permissions.RejectDescriptor = true;

            await Assert.ThrowsAsync<InvalidDataException>(() => provider.RestoreAsync(archivePath, target, true, null, null, default));

            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target, "data.txt")));
            Assert.Empty(permissions.Applied);
            Assert.Empty(Directory.EnumerateDirectories(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("a/..", false)]
    [InlineData("a/..", true)]
    [InlineData("dir/../a/..", false)]
    [InlineData("a/../data.txt", false)]
    [InlineData("a", false)]
    [InlineData("x", false)]
    [InlineData("x/..", false)]
    public async Task Planned_link_chains_are_rejected_before_any_destination_replacement(string linkTarget, bool reverseLinks)
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-planned-links-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.Combine(target, "dir"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "replacement");
            await File.WriteAllTextAsync(Path.Combine(target, "data.txt"), "original");
            var permissions = new RecordingWindowsPermissions();
            var provider = new FileSystemBackupProvider([], permissions);
            var (_, archivePath) = await provider.CreateBackupAsync(new(source, ["**"], []), staging, null, default);
            FileManifestEntry[] links =
            [
                new("a", 0, "", DateTimeOffset.UtcNow, IsSymlink: true, SymlinkTarget: ".", IsDirectory: true),
                new("x", 0, "", DateTimeOffset.UtcNow, IsSymlink: true, SymlinkTarget: linkTarget, IsDirectory: true)
            ];
            AddManifestLinks(archivePath, reverseLinks ? links.Reverse().ToArray() : links);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => provider.RestoreAsync(archivePath, target, true, null, null, default));
            Assert.Equal("A symbolic link target traverses another symbolic link or reparse point.", error.Message);

            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target, "data.txt")));
            Assert.Empty(permissions.Applied);
            Assert.Equal(["data.txt", "dir"], Directory.EnumerateFileSystemEntries(target).Select(path => Path.GetFileName(path)!).Order().ToArray());
            await provider.RestoreAsync(archivePath, target, true, ["data.txt"], null, default);
            Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(target, "data.txt")));
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.RestoreAsync(archivePath, target, true, ["a", "x"], null, default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [LinuxFact]
    public async Task Existing_links_hidden_by_dotdot_are_rejected_including_dangling_and_cyclic_links()
    {
        foreach (var existingTarget in new[] { ".", "../outside", "missing", "a" })
        foreach (var linkTarget in new[] { "a/..", "a/../data.txt", "dir/../a/../data.txt" })
        {
            var root = Path.Combine(Path.GetTempPath(), "bp-existing-links-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "source");
            var staging = Path.Combine(root, "staging");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(Path.Combine(target, "dir"));
            Directory.CreateDirectory(Path.Combine(root, "outside"));
            try
            {
                await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "replacement");
                await File.WriteAllTextAsync(Path.Combine(target, "data.txt"), "original");
                Directory.CreateSymbolicLink(Path.Combine(target, "a"), existingTarget);
                var permissions = new RecordingWindowsPermissions();
                var provider = new FileSystemBackupProvider([], permissions);
                var (_, archivePath) = await provider.CreateBackupAsync(new(source, ["**"], []), staging, null, default);
                AddManifestLinks(archivePath, new FileManifestEntry("x", 0, "", DateTimeOffset.UtcNow, IsSymlink: true, SymlinkTarget: linkTarget, IsDirectory: true));

                await Assert.ThrowsAnyAsync<IOException>(() => provider.RestoreAsync(archivePath, target, true, null, null, default));

                Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target, "data.txt")));
                Assert.Equal(existingTarget, new DirectoryInfo(Path.Combine(target, "a")).LinkTarget);
                Assert.Empty(permissions.Applied);
                Assert.False(Directory.Exists(Path.Combine(target, "x")));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    [LinuxFact]
    public async Task Preserved_confined_links_roundtrip_with_filters_and_explicit_selection()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-confined-links-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        var selectedTarget = Path.Combine(root, "selected-target");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(selectedTarget);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "confined content");
            await File.WriteAllTextAsync(Path.Combine(source, "ignored.log"), "excluded");
            File.CreateSymbolicLink(Path.Combine(source, "sub", "file-link"), "../data.txt");
            Directory.CreateSymbolicLink(Path.Combine(source, "sub", "root-link"), "..");
            File.CreateSymbolicLink(Path.Combine(source, "dangling-link"), "not-created");
            Directory.CreateSymbolicLink(Path.Combine(source, "root-link"), ".");
            var provider = new FileSystemBackupProvider([], null);
            var (manifest, archivePath) = await provider.CreateBackupAsync(
                new(source, ["**"], ["*.log"], FileSymlinkPolicy.Preserve), staging, null, default);

            Assert.Equal(4, manifest.Files.Count(entry => entry.IsSymlink));
            await provider.RestoreAsync(archivePath, target, false, null, null, default);

            Assert.Equal("confined content", await File.ReadAllTextAsync(Path.Combine(target, "sub", "file-link")));
            Assert.Equal("..", new DirectoryInfo(Path.Combine(target, "sub", "root-link")).LinkTarget);
            Assert.Equal(".", new DirectoryInfo(Path.Combine(target, "root-link")).LinkTarget);
            Assert.Equal("not-created", new FileInfo(Path.Combine(target, "dangling-link")).LinkTarget);
            Assert.False(File.Exists(Path.Combine(target, "ignored.log")));
            await provider.RestoreAsync(archivePath, selectedTarget, false, ["sub/file-link"], null, default);
            Assert.Equal("../data.txt", new FileInfo(Path.Combine(selectedTarget, "sub", "file-link")).LinkTarget);
            Assert.False(File.Exists(Path.Combine(selectedTarget, "data.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AddManifestLinks(string archivePath, params FileManifestEntry[] links)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry("manifest.json")!;
        FileBackupManifest manifest;
        using (var stream = entry.Open()) manifest = JsonSerializer.Deserialize<FileBackupManifest>(stream)!;
        entry.Delete();
        using var output = archive.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(output, manifest with { Files = [.. manifest.Files, .. links] });
    }

    private sealed class RecordingWindowsPermissions : IWindowsFilePermissions
    {
        internal const string Descriptor = "D:P(A;;FA;;;SY)";
        internal bool RejectDescriptor { get; set; }
        internal List<(string Path, bool Directory)> Applied { get; } = [];
        public string CaptureAccessControl(string path, bool isDirectory) => Descriptor;
        public void ValidateAccessControl(string descriptor)
        {
            if (RejectDescriptor || descriptor != Descriptor) throw new InvalidDataException("Invalid test descriptor.");
        }
        public void ApplyAccessControl(string path, bool isDirectory, string descriptor)
        {
            ValidateAccessControl(descriptor);
            Assert.True(isDirectory ? Directory.Exists(path) : File.Exists(path));
            Applied.Add((path, isDirectory));
        }
        public void CreatePrivateDirectory(string path) => Directory.CreateDirectory(path);
    }
}
