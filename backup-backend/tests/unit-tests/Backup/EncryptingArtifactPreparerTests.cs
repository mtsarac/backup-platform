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

    private ArtifactPreparationInput Input(byte[] data, ArtifactEncryption? encryption)
    {
        File.WriteAllBytes(Path.Combine(_root, "source.bak"), data);
        return new ArtifactPreparationInput(new AgentStagingDirectory(new AgentPath(_root)), "source.bak", DatabaseBackupType.Full,
            new AppliedFullBackupOptions(false, true, false), encryption);
    }

    private ArtifactEncryption ForKey(bool required = true) =>
        new(required, [new BpeRecipient(BpeRecipientType.TenantWrap, 1, _key)]);

    [Fact]
    public async Task EncryptsIntoANewStagedFileAndLeavesNoPlaintextInIt()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        var input = Input(data, ForKey());

        var prepared = await new EncryptingArtifactPreparer().PrepareAsync(input, CancellationToken.None);

        Assert.False(prepared.IsSourceFile);
        Assert.Equal(ArtifactTransformKind.Encryption, Assert.Single(prepared.Transforms).Kind);
        var artifact = await File.ReadAllBytesAsync(input.Staging.FilePath(prepared.FileName));
        Assert.Equal(-1, artifact.AsSpan().IndexOf(data.AsSpan(1000, 64)));
        using var plain = new MemoryStream();
        await BpeDecryptor.DecryptAsync(new MemoryStream(artifact), plain, _key);
        Assert.Equal(data, plain.ToArray());
    }

    [Fact]
    public async Task ReportsTheTenantWrapRecipientOfTheFileItWrote()
    {
        var prepared = await new EncryptingArtifactPreparer().PrepareAsync(Input([1, 2, 3], ForKey()), CancellationToken.None);

        var seal = Assert.IsType<ArtifactSeal>(prepared.Seal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_key.ExportSubjectPublicKeyInfo())[..16]), seal.KeyId);
        Assert.Equal(1, seal.KeyVersion);
        Assert.Equal(32, DekOf(seal));
    }

    private int DekOf(ArtifactSeal seal) => _key.Decrypt(seal.WrappedDek, RSAEncryptionPadding.OaepSHA256).Length;

    [Theory]
    [InlineData(false)] // no encryption instruction at all
    [InlineData(true)] // optional instruction without recipients
    public async Task WithoutRequiredEncryptionTheSourceFileIsTheArtifact(bool optionalWithoutRecipients)
    {
        var input = Input([1, 2, 3], optionalWithoutRecipients ? new ArtifactEncryption(false, []) : null);

        var prepared = await new EncryptingArtifactPreparer().PrepareAsync(input, CancellationToken.None);

        Assert.True(prepared.IsSourceFile);
        Assert.Equal("source.bak", prepared.FileName);
        Assert.Empty(prepared.Transforms);
        Assert.Null(prepared.Seal);
    }

    [Fact]
    public async Task RequiredEncryptionWithoutRecipientsFailsInsteadOfProducingPlaintext()
    {
        var input = Input([1, 2, 3], new ArtifactEncryption(true, []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new EncryptingArtifactPreparer().PrepareAsync(input, CancellationToken.None));

        Assert.Equal(["source.bak"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ACancelledEncryptionLeavesNoPartialArtifactBehind()
    {
        var input = Input(new byte[10_000], ForKey());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EncryptingArtifactPreparer().PrepareAsync(input, cts.Token));

        Assert.Equal(["source.bak"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }
}
