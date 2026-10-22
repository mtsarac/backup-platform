namespace BackupPlatform.Domain.Backups;

public sealed record FileBackupPolicy
{
    public IReadOnlyList<string> Paths { get; init; } = ["."];
    public IReadOnlyList<string> Includes { get; init; } = ["**"];
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public string SymlinkPolicy { get; init; } = "Skip";

    public FileBackupPolicy Validate()
    {
        if (Paths is null || Paths.Count is < 1 or > 256 || Includes is null || Excludes is null || Includes.Count > 256 || Excludes.Count > 256
            || SymlinkPolicy is not ("Skip" or "Preserve")) throw new ArgumentException("Invalid file selection.");
        foreach (var path in Paths) ValidatePath(path, allowRoot: true);
        foreach (var pattern in Includes.Concat(Excludes))
        {
            ValidatePath(pattern, allowRoot: false);
        }
        return this;
    }

    public static void ValidatePath(string path, bool allowRoot)
    {
        if (allowRoot && path == ".") return;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(part => part is "" or "." or "..")) throw new ArgumentException("File paths and filters must be confined relative forward-slash paths.");
    }

    public bool Equals(FileBackupPolicy? other) => other is not null && SymlinkPolicy == other.SymlinkPolicy
        && Paths.SequenceEqual(other.Paths) && Includes.SequenceEqual(other.Includes) && Excludes.SequenceEqual(other.Excludes);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SymlinkPolicy);
        foreach (var path in Paths) hash.Add(path);
        foreach (var pattern in Includes) hash.Add(pattern);
        foreach (var pattern in Excludes) hash.Add(pattern);
        return hash.ToHashCode();
    }
}
