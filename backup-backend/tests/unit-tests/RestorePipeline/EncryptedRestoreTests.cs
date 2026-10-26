using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Encryption;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.RestorePipeline;

public sealed class EncryptedRestoreTests : IDisposable
{
    private static readonly RSA TenantKey = RSA.Create(4096);

    private readonly RestorePipelineHarness _h = new();
    private readonly byte[] _dek = RandomNumberGenerator.GetBytes(32);
    private byte[] _ciphertext = [];

    public void Dispose() => _h.Dispose();

    private async Task<DatabaseRestorePipelineRequest> EncryptedRequestAsync(
        Func<byte[]>? unwrap = null, ContentChecksum? sourceChecksum = null, Action<byte[]>? tamper = null)
    {
        using var output = new MemoryStream();
        await BpeEncryptor.EncryptAsync(new MemoryStream(RestorePipelineHarness.BackupBytes), output, RestorePipelineHarness.BackupBytes.Length,
            [new BpeRecipient(BpeRecipientType.TenantWrap, 1, TenantKey)], new BpeEncryptOptions(Dek: (byte[])_dek.Clone()));
        _ciphertext = output.ToArray();
        tamper?.Invoke(_ciphertext);
        _h.Storage.Objects[_h.Key.Value] = _ciphertext;
        return _h.Request(size: _ciphertext.Length, checksum: RestorePipelineHarness.ChecksumOf(_ciphertext)) with
        {
            Decryption = new ArtifactDecryption(sourceChecksum ?? RestorePipelineHarness.ChecksumOf(RestorePipelineHarness.BackupBytes),
                _ =>
                {
                    Unwraps++;
                    return Task.FromResult(unwrap?.Invoke() ?? (byte[])_dek.Clone());
                })
        };
    }

    private int Unwraps { get; set; }

    private void AssertFailedBeforeTheEngineSawAnything(DatabaseRestorePipelineResult result)
    {
        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Empty(_h.Provider.Inspections);
        Assert.Empty(_h.Provider.Executions);
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task ADecryptedBackupIsWhatTheEngineInspectsAndRestoresAndNoCiphertextIsLeftBehind()
    {
        var result = await _h.RunAsync(await EncryptedRequestAsync());

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        var staged = Assert.Single(_h.Provider.StagedAtInspect);
        Assert.Equal(RestorePipelineHarness.BackupBytes, staged.Bytes);
        Assert.True(staged.Held);
        Assert.Equal(1, Unwraps);
        Assert.Single(_h.Provider.Executions);
        Assert.Equal(StagingCleanupStatus.Deleted, result.StagingCleanup.Status);
        Assert.Empty(_h.StagedFiles);
        Assert.True(result.ChecksumVerified);
    }

    [Fact]
    public async Task ThePlainReplicaPathIsUnchangedWhenNoDecryptionIsRequested()
    {
        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(RestorePipelineHarness.BackupBytes, Assert.Single(_h.Provider.StagedAtInspect).Bytes);
    }

    [Fact]
    public async Task ATamperedCiphertextWithAMatchingStorageChecksumFailsBeforeTheEngineIsAsked()
    {
        var request = await EncryptedRequestAsync(tamper: bytes => bytes[^20] ^= 1);

        var result = await _h.RunAsync(request);

        AssertFailedBeforeTheEngineSawAnything(result);
        Assert.Equal(RestoreAgentFailureCodes.ChecksumMismatch, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ArtifactAuthenticationFailed, result.Failure.ReasonCode);
        Assert.Equal(RestoreExecutionStage.VerifyingChecksum, result.Failure.Stage);
    }

    [Fact]
    public async Task ACiphertextChangedInStorageIsStillCaughtByTheCiphertextChecksumFirst()
    {
        var request = await EncryptedRequestAsync();
        var changed = (byte[])_ciphertext.Clone();
        changed[^5] ^= 1;
        _h.Storage.Objects[_h.Key.Value] = changed;

        var result = await _h.RunAsync(request);

        AssertFailedBeforeTheEngineSawAnything(result);
        Assert.Equal(RestorePipelineReasons.ReplicaChecksumMismatch, result.Failure!.ReasonCode);
        Assert.Equal(0, Unwraps);
    }

    [Fact]
    public async Task ADecryptedBackupThatDoesNotMatchTheRecordedSourceChecksumIsNeverRestored()
    {
        var request = await EncryptedRequestAsync(sourceChecksum: ContentChecksum.Sha256(SHA256.HashData([1, 2, 3])));

        var result = await _h.RunAsync(request);

        AssertFailedBeforeTheEngineSawAnything(result);
        Assert.Equal(RestoreAgentFailureCodes.ChecksumMismatch, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.SourceChecksumMismatch, result.Failure.ReasonCode);
    }

    [Fact]
    public async Task AWrongKeyFailsAuthenticationAndTheKeyIsZeroedAfterwards()
    {
        var wrong = RandomNumberGenerator.GetBytes(32);
        var request = await EncryptedRequestAsync(unwrap: () => wrong);

        var result = await _h.RunAsync(request);

        AssertFailedBeforeTheEngineSawAnything(result);
        Assert.Equal(RestorePipelineReasons.ArtifactAuthenticationFailed, result.Failure!.ReasonCode);
        Assert.All(wrong, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task TheKeyHandedOutByTheServiceIsZeroedAfterASuccessfulDecryption()
    {
        var handedOut = (byte[])_dek.Clone();
        var request = await EncryptedRequestAsync(unwrap: () => handedOut);

        var result = await _h.RunAsync(request);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.All(handedOut, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task AKeyServiceFailureFailsClosedAndNeverFallsBackToTheCiphertext()
    {
        var request = (await EncryptedRequestAsync()) with
        {
            Decryption = new ArtifactDecryption(RestorePipelineHarness.ChecksumOf(RestorePipelineHarness.BackupBytes),
                _ => throw new ArtifactKeyUnavailableException("refused"))
        };

        var result = await _h.RunAsync(request);

        AssertFailedBeforeTheEngineSawAnything(result);
        Assert.Equal(RestoreAgentFailureCodes.ArtifactKeyUnavailable, result.Failure!.Code);
        Assert.Equal(RestorePipelineReasons.ArtifactKeyUnavailable, result.Failure.ReasonCode);
        Assert.DoesNotContain("refused", result.Failure.Message);
    }

    [Fact]
    public async Task APlainFileLabelledEncryptedIsRejectedAsNotAnArtifact()
    {
        var decryption = (await EncryptedRequestAsync()).Decryption;
        _h.Storage.Objects[_h.Key.Value] = RestorePipelineHarness.BackupBytes;
        var request = _h.Request() with { Decryption = decryption };

        var result = await _h.RunAsync(request);

        AssertFailedBeforeTheEngineSawAnything(result);
        Assert.Equal(RestorePipelineReasons.ArtifactAuthenticationFailed, result.Failure!.ReasonCode);
        Assert.Equal(0, Unwraps);
    }
}
