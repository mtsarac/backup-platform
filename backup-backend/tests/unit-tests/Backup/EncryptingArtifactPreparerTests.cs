using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Encryption;

namespace BackupPlatform.UnitTests.Backup;

public sealed class EncryptingArtifactPreparerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bp-enc-").FullName;
    private readonly RSA _key = RSA.Create(3072);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class Recipients(params BpeRecipient[] list) : IArtifactRecipientSource
    {
        public IReadOnlyList<BpeRecipient> GetRecipients() => list;
    }

    private ArtifactPreparationInput Input(byte[] data)
    {
        File.WriteAllBytes(Path.Combine(_root, "source.bak"), data);
        return new ArtifactPreparationInput(new AgentStagingDirectory(new AgentPath(_root)), "source.bak", DatabaseBackupType.Full,
            new AppliedFullBackupOptions(false, true, false));
    }

    [Fact]
    public async Task EncryptsIntoANewStagedFileAndLeavesNoPlaintextInIt()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        var input = Input(data);

        var prepared = await new EncryptingArtifactPreparer(new Recipients(new BpeRecipient(BpeRecipientType.TenantWrap, 1, _key)))
            .PrepareAsync(input, CancellationToken.None);

        Assert.False(prepared.IsSourceFile);
        Assert.Equal(ArtifactTransformKind.Encryption, Assert.Single(prepared.Transforms).Kind);
        var artifact = await File.ReadAllBytesAsync(input.Staging.FilePath(prepared.FileName));
        Assert.Equal(-1, artifact.AsSpan().IndexOf(data.AsSpan(1000, 64)));
        using var plain = new MemoryStream();
        await BpeDecryptor.DecryptAsync(new MemoryStream(artifact), plain, _key);
        Assert.Equal(data, plain.ToArray());
    }

    [Fact]
    public async Task WithoutRecipientsTheSourceFileIsTheArtifact()
    {
        var input = Input([1, 2, 3]);

        var prepared = await new EncryptingArtifactPreparer(new Recipients()).PrepareAsync(input, CancellationToken.None);

        Assert.True(prepared.IsSourceFile);
        Assert.Equal("source.bak", prepared.FileName);
        Assert.Empty(prepared.Transforms);
    }

    [Fact]
    public async Task ACancelledEncryptionLeavesNoPartialArtifactBehind()
    {
        var input = Input(new byte[10_000]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EncryptingArtifactPreparer(new Recipients(new BpeRecipient(BpeRecipientType.TenantWrap, 1, _key))).PrepareAsync(input, cts.Token));

        Assert.Equal(["source.bak"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }
}
