using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.UnitTests.Backup;

public sealed class DatabaseEnginesTests
{
    [Theory]
    [InlineData("mssql", true)]
    [InlineData("postgres", true)]
    [InlineData("file-system", true)]
    [InlineData("mysql", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheThreeSupportedEnginesAreValidOnBothSidesOfTheWire(string? engine, bool valid)
    {
        Assert.Equal(valid, BackupPlatform.Domain.Backups.DatabaseEngines.IsValid(engine));
        Assert.Equal(valid, DatabaseEngineKeys.IsValid(engine));
    }
}
