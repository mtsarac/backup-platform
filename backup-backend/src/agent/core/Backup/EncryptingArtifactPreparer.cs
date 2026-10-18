using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>Public wrap keys the artifact's DEK is sealed to. Empty means this backup is not encrypted. Never carries private key material.</summary>
public interface IArtifactRecipientSource
{
    IReadOnlyList<BpeRecipient> GetRecipients();
}

/// <summary>
/// Encrypts the verified engine backup into a <c>bpe</c> v2 file in staging, so storage providers, relay and replicas only ever see ciphertext.
/// With no recipients it behaves exactly like <see cref="PassThroughArtifactPreparer"/>. The plaintext source file stays the pipeline's to remove.
/// </summary>
public sealed class EncryptingArtifactPreparer(IArtifactRecipientSource recipients) : IArtifactPreparer
{
    public const string Algorithm = "bpe-v2/AES-256-GCM-STREAM";

    public async Task<PreparedArtifact> PrepareAsync(ArtifactPreparationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var wrapKeys = recipients.GetRecipients();
        if (wrapKeys.Count == 0)
        {
            return await new PassThroughArtifactPreparer().PrepareAsync(input, cancellationToken).ConfigureAwait(false);
        }

        var staging = input.Staging;
        var outputName = $"{Guid.NewGuid():N}.bpe";
        var sourcePath = staging.FilePath(input.SourceFileName);
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var output = new FileStream(staging.FilePath(outputName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await BpeEncryptor.EncryptAsync(source, output, source.Length, wrapKeys, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A half-written ciphertext must never be mistaken for an artifact.
            staging.TryDeleteFile(outputName);
            throw;
        }

        return new PreparedArtifact(outputName, IsSourceFile: false, [new ArtifactTransform(ArtifactTransformKind.Encryption, Algorithm)]);
    }
}
