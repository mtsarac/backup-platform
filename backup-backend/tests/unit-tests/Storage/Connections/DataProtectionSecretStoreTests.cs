using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.SecretProviders.DataProtection;
using Microsoft.AspNetCore.DataProtection;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class DataProtectionSecretStoreTests
{
    private const string Plaintext = "s3cr3t-PLAINTEXT-value/with+symbols==";

    private sealed class InMemoryCiphertextRepository : ISecretCiphertextRepository
    {
        public Dictionary<string, string> Rows { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Rows.TryGetValue(name, out var value) ? value : null);

        public Task SetAsync(string name, string ciphertext, CancellationToken cancellationToken)
        {
            Rows[name] = ciphertext;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Rows.Remove(name));
    }

    private readonly InMemoryCiphertextRepository _repository = new();
    private readonly EphemeralDataProtectionProvider _protection = new();

    private DataProtectionSecretStore Store(IDataProtectionProvider? protection = null) => new(protection ?? _protection, _repository);

    private static SecretReference Ref(string name) => new(name);

    [Fact]
    public async Task ASecretRoundTripsExactly()
    {
        var store = Store();

        await store.SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);
        var resolved = await store.ResolveAsync(Ref("sc-1"), CancellationToken.None);

        Assert.Equal(Plaintext, resolved.Reveal());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ünïcödé ✓ 日本語")]
    [InlineData("{\"password\":\"a\\\"b\"}")]
    public async Task EdgeCaseValuesRoundTripUnchanged(string value)
    {
        var store = Store();

        await store.SetAsync(Ref("sc-edge"), new SecretValue(value), CancellationToken.None);

        Assert.Equal(value, (await store.ResolveAsync(Ref("sc-edge"), CancellationToken.None)).Reveal());
    }

    [Fact]
    public async Task TheRepositoryOnlyEverSeesCiphertext()
    {
        await Store().SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);

        var ciphertext = Assert.Single(_repository.Rows).Value;

        Assert.DoesNotContain(Plaintext, ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAINTEXT", ciphertext, StringComparison.Ordinal);
        Assert.NotEmpty(ciphertext);
    }

    [Fact]
    public async Task EncryptingTheSameValueTwiceProducesDifferentCiphertext()
    {
        var store = Store();

        await store.SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);
        var first = _repository.Rows["sc-1"];
        await store.SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);

        Assert.NotEqual(first, _repository.Rows["sc-1"]);
    }

    [Fact]
    public async Task OverwritingReplacesTheValueUnderTheSameReference()
    {
        var store = Store();
        await store.SetAsync(Ref("sc-1"), new SecretValue("first"), CancellationToken.None);

        await store.SetAsync(Ref("sc-1"), new SecretValue("second"), CancellationToken.None);

        Assert.Equal("second", (await store.ResolveAsync(Ref("sc-1"), CancellationToken.None)).Reveal());
        Assert.Single(_repository.Rows);
    }

    [Fact]
    public async Task ExistsAndDeleteReflectWhatIsStored()
    {
        var store = Store();
        Assert.False(await store.ExistsAsync(Ref("sc-1"), CancellationToken.None));

        await store.SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);
        Assert.True(await store.ExistsAsync(Ref("sc-1"), CancellationToken.None));
        Assert.False(await store.ExistsAsync(Ref("sc-2"), CancellationToken.None));

        Assert.True(await store.DeleteAsync(Ref("sc-1"), CancellationToken.None));
        Assert.False(await store.ExistsAsync(Ref("sc-1"), CancellationToken.None));
        Assert.False(await store.DeleteAsync(Ref("sc-1"), CancellationToken.None));
    }

    [Fact]
    public async Task ResolvingAMissingOrDeletedReferenceIsSecretUnavailable()
    {
        var store = Store();
        await store.SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);
        await store.DeleteAsync(Ref("sc-1"), CancellationToken.None);

        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await store.ResolveAsync(Ref("sc-1"), CancellationToken.None));
        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await store.ResolveAsync(Ref("never-written"), CancellationToken.None));
    }

    [Fact]
    public async Task ACiphertextCopiedUnderAnotherReferenceDoesNotDecrypt()
    {
        var store = Store();
        await store.SetAsync(Ref("tenant-a-secret"), new SecretValue(Plaintext), CancellationToken.None);

        _repository.Rows["tenant-b-secret"] = _repository.Rows["tenant-a-secret"];

        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await store.ResolveAsync(Ref("tenant-b-secret"), CancellationToken.None));
        Assert.Equal(Plaintext, (await store.ResolveAsync(Ref("tenant-a-secret"), CancellationToken.None)).Reveal());
    }

    [Fact]
    public async Task ATamperedCiphertextIsSecretUnavailable()
    {
        var store = Store();
        await store.SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);
        var original = _repository.Rows["sc-1"];
        var middle = original.Length / 2;
        var flipped = original[middle] == 'A' ? 'B' : 'A';
        _repository.Rows["sc-1"] = original[..middle] + flipped + original[(middle + 1)..];

        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await store.ResolveAsync(Ref("sc-1"), CancellationToken.None));
    }

    [Theory]
    [InlineData("not a ciphertext at all")]
    [InlineData("")]
    [InlineData("AAAA")]
    public async Task GarbageInTheRepositoryIsSecretUnavailableNotACryptoExceptionOrTheRawValue(string garbage)
    {
        _repository.Rows["sc-1"] = garbage;

        var exception = await Assert.ThrowsAsync<SecretUnavailableException>(async () => await Store().ResolveAsync(Ref("sc-1"), CancellationToken.None));

        Assert.Equal("The referenced secret is unavailable.", exception.Message);
    }

    [Fact]
    public async Task ACiphertextWrittenUnderAnotherKeyRingCannotBeDecrypted()
    {
        await Store().SetAsync(Ref("sc-1"), new SecretValue(Plaintext), CancellationToken.None);

        var otherKeyRing = Store(new EphemeralDataProtectionProvider());

        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await otherKeyRing.ResolveAsync(Ref("sc-1"), CancellationToken.None));
    }

    [Fact]
    public async Task NullArgumentsAreRejected()
    {
        var store = Store();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.ResolveAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetAsync(null!, new SecretValue("x"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetAsync(Ref("sc-1"), null!, CancellationToken.None));
    }

    [Fact]
    public void SecretValueNeverRendersItsContent()
    {
        var value = new SecretValue(Plaintext);

        Assert.Equal("[redacted]", value.ToString());
        Assert.DoesNotContain("PLAINTEXT", $"{value}", StringComparison.Ordinal);
        Assert.Equal(Plaintext, value.Reveal());
        Assert.Throws<ArgumentNullException>(() => new SecretValue(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("slash/ed")]
    public void SecretReferencesRejectNamesThatCouldBeMistakenForPaths(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SecretReference(name));
    }
}
