using BackupPlatform.Domain.Storage;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Storage;

public sealed class StorageProviderTypesTests
{
    [Theory]
    [InlineData("local", false, false)]
    [InlineData("central", false, false)]
    [InlineData("sftp", true, true)]
    [InlineData("google-drive", true, true)]
    [InlineData("s3", false, true)]
    [InlineData("smb", false, true)]
    [InlineData("unknown", false, false)]
    public void CapabilityTableMatchesWhoExecutesAndWhoHoldsCredentialsOnBothSidesOfTheWire(string provider, bool agentExecuted, bool external)
    {
        Assert.Equal(agentExecuted, StorageProviderTypes.AgentExecuted(provider));
        Assert.Equal(external, StorageProviderTypes.External(provider));
        Assert.Equal(agentExecuted, AgentStorageProviders.AgentExecuted(provider));
        Assert.Equal(external, AgentStorageProviders.External(provider));
    }
}
