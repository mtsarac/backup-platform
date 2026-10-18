using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// Encrypts the verified engine backup into a <c>bpe</c> v2 file in staging, so storage providers, relay and replicas only ever see ciphertext.
/// Without an encryption instruction (or an optional one with no recipients) it behaves like <see cref="PassThroughArtifactPreparer"/>; a required
/// one with no recipients fails instead of producing plaintext. The plaintext source file stays the pipeline's to remove.
/// </summary>
public sealed class EncryptingArtifactPreparer : IArtifactPreparer
{
    public const string Algorithm = "bpe-v2/AES-256-GCM-STREAM";

    public async Task<PreparedArtifact> PrepareAsync(ArtifactPreparationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var wrapKeys = input.Encryption?.Recipients ?? [];
        if (input.Encryption is { Required: true } && wrapKeys.Count == 0)
        {
            throw new InvalidOperationException("Encryption is required for this backup but no recipient keys were provided.");
        }

        if (wrapKeys.Count == 0)
        {
            return await new PassThroughArtifactPreparer().PrepareAsync(input, cancellationToken).ConfigureAwait(false);
        }

        var staging = input.Staging;
        var outputName = input.SourceFileName + ".bpe"; // stays inside ManagedBackupLayout's grammar so storage keys and staging cleanup still recognise it
        var sourcePath = staging.FilePath(input.SourceFileName);
        var outputPath = staging.FilePath(outputName);
        ArtifactSeal? seal;
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await BpeEncryptor.EncryptAsync(source, output, source.Length, wrapKeys, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await using var written = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var recipients = await BpeDecryptor.ReadRecipientsAsync(written, cancellationToken).ConfigureAwait(false);
            seal = recipients.Where(recipient => recipient.Type == BpeRecipientType.TenantWrap)
                .Select(recipient => new ArtifactSeal(recipient.KeyId, recipient.KeyVersion, recipient.WrappedDek)).FirstOrDefault();
        }
        catch
        {
            // A half-written ciphertext must never be mistaken for an artifact.
            staging.TryDeleteFile(outputName);
            throw;
        }

        return new PreparedArtifact(outputName, IsSourceFile: false, [new ArtifactTransform(ArtifactTransformKind.Encryption, Algorithm)], seal);
    }
}
