using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.FileSystem;

namespace BackupPlatform.UnitTests.Storage;

public sealed class StorageIoErrorsTests
{
    private const string SecretPath = "/srv/secret-share/tenant/db.bak";

    [Theory]
    [InlineData(28)]
    [InlineData(122)]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    public void DiskFullErrorsMapToInsufficientSpace(int hresult)
    {
        var original = new IOException("No space left on device: " + SecretPath, hresult);

        var mapped = StorageIoErrors.Map(original);

        Assert.Equal(StorageFailureKind.InsufficientSpace, mapped.Kind);
        Assert.Same(original, mapped.InnerException);
        Assert.DoesNotContain(SecretPath, mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnauthorizedAccessMapsToAccessDenied()
    {
        var original = new UnauthorizedAccessException("Access to the path '" + SecretPath + "' is denied.");

        var mapped = StorageIoErrors.Map(original);

        Assert.Equal(StorageFailureKind.AccessDenied, mapped.Kind);
        Assert.Same(original, mapped.InnerException);
        Assert.DoesNotContain(SecretPath, mapped.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(unchecked((int)0x80070005))]
    public void OtherIoErrorsMapToIo(int hresult)
    {
        var original = new IOException("broken " + SecretPath, hresult);

        var mapped = StorageIoErrors.Map(original);

        Assert.Equal(StorageFailureKind.Io, mapped.Kind);
        Assert.Same(original, mapped.InnerException);
        Assert.DoesNotContain(SecretPath, mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivedIoExceptionsAreStillIo()
    {
        Assert.Equal(StorageFailureKind.Io, StorageIoErrors.Map(new PathTooLongException(SecretPath)).Kind);
        Assert.Equal(StorageFailureKind.Io, StorageIoErrors.Map(new DirectoryNotFoundException(SecretPath)).Kind);
    }
}
