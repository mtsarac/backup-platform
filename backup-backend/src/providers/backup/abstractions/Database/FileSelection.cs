namespace BackupPlatform.BackupProviders.Abstractions.Database;

public sealed record FileBackupSelection(IReadOnlyList<string> Paths, IReadOnlyList<string> Includes, IReadOnlyList<string> Excludes, string SymlinkPolicy);
public sealed record FileRestoreSelection(IReadOnlyList<string>? Paths, string RelativeDirectory);
