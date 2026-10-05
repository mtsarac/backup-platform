using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>What the preparation step receives: the verified engine backup file in the staging directory.</summary>
public sealed record ArtifactPreparationInput(
    AgentStagingDirectory Staging,
    string SourceFileName,
    DatabaseBackupType BackupType,
    AppliedFullBackupOptions AppliedOptions);

/// <summary>
/// The artifact that will be checksummed and stored. A preparer that produces a new file (compressed, encrypted) writes it
/// into the same staging directory and names it by <see cref="FileName"/>, a simple name; the pipeline owns removing it. A preparer that
/// leaves the engine's file alone returns the source name and <see cref="IsSourceFile"/> = true.
/// </summary>
public sealed record PreparedArtifact(string FileName, bool IsSourceFile, IReadOnlyList<ArtifactTransform> Transforms);

/// <summary>
/// The seam where compression and encryption of the stored artifact will go. No transform exists in this iteration: the
/// engine's native backup compression already shrinks the file, and file encryption is deliberately out of scope. Adding
/// one means a new <see cref="IArtifactPreparer"/>; neither the result model nor the pipeline changes.
/// </summary>
public interface IArtifactPreparer
{
    Task<PreparedArtifact> PrepareAsync(ArtifactPreparationInput input, CancellationToken cancellationToken);
}

/// <summary>The artifact is the engine's backup file as written: same file, no transforms.</summary>
public sealed class PassThroughArtifactPreparer : IArtifactPreparer
{
    public Task<PreparedArtifact> PrepareAsync(ArtifactPreparationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PreparedArtifact(input.SourceFileName, IsSourceFile: true, []));
    }
}
