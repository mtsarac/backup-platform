using System.Text.Json;
using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Core.Backup;

internal sealed record RetainedTransfer(BackupArtifactInfo Artifact, BackupSourceFile SourceFile, DatabaseBackupSource? Source,
    BackupOptionsOutcome? Options, BackupVerification Verification, DateTimeOffset StartedAt, DateTimeOffset? BackupStartedAt, DateTimeOffset? BackupFinishedAt)
{
    private static string Manifest(AgentStagingDirectory directory, Guid attemptId) => directory.FilePath($"transfer-{attemptId:N}.json");
    internal static bool IsPinned(AgentStagingDirectory directory, string name) => File.Exists(directory.FilePath(name + ".resume-pin"));
    internal void Save(AgentStagingDirectory directory, Guid attemptId)
    {
        if (attemptId == Guid.Empty) return;
        foreach (var name in new[] { Artifact.FileName, SourceFile.FileName }.Distinct())
        {
            using var pin = Create(directory.FilePath(name + ".resume-pin"));
            pin.Flush(true);
        }
        var path = Manifest(directory, attemptId);
        var temporary = path + ".new";
        using (var file = Create(temporary))
        {
            JsonSerializer.Serialize(file, this);
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    internal static RetainedTransfer Load(AgentStagingDirectory directory, Guid attemptId)
    {
        using var file = File.OpenRead(Manifest(directory, attemptId));
        return JsonSerializer.Deserialize<RetainedTransfer>(file) ?? throw new IOException("The retained artifact journal is invalid.");
    }
    internal void Release(AgentStagingDirectory directory, Guid attemptId)
    {
        if (attemptId == Guid.Empty) return;
        foreach (var name in new[] { Artifact.FileName, SourceFile.FileName }.Distinct()) File.Delete(directory.FilePath(name + ".resume-pin"));
        File.Delete(Manifest(directory, attemptId));
    }
    private static FileStream Create(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}

internal sealed class TransferPausedException(string reason) : Exception
{
    internal string Reason { get; } = reason;
}
