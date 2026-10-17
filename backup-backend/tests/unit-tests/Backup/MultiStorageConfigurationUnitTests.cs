using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class MultiStorageConfigurationUnitTests
{
    private static BackupConfiguration CreateLegacy() =>
        new("mssql", "SalesDB", "sql.local", 1433, true, null, true, false, "default", "", true, "Preferred", "Preferred");

    [Fact]
    public void SingleDestinationConfiguration_ValidatesAndDefaultsReplicaCount()
    {
        var connId = Guid.NewGuid();
        var config = (CreateLegacy() with { Destinations = [new BackupDestination("sftp", connId, IsPrimary: true)] }).Validate();

        var dest = Assert.Single(config.Destinations);
        Assert.Equal(connId, dest.StorageConnectionId);
        Assert.Equal(1, config.RequiredVerifiedReplicaCount);
    }

    [Fact]
    public void MultiDestinationConfiguration_ValidatesSuccessfully()
    {
        var sftpConn = Guid.NewGuid();
        var gdriveConn = Guid.NewGuid();

        var config = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true),
                new BackupDestination("sftp", sftpConn, IsPrimary: false),
                new BackupDestination("google-drive", gdriveConn, IsPrimary: false)
            ],
            RequiredVerifiedReplicaCount = 2
        };

        var validated = config.Validate();
        Assert.Equal(3, validated.Destinations.Count);
        Assert.Equal(2, validated.RequiredVerifiedReplicaCount);
    }

    [Theory]
    [InlineData(false, false)] // no primary
    [InlineData(true, true)] // multiple primaries
    public void MultiDestination_RequiresExactlyOnePrimary(bool localIsPrimary, bool sftpIsPrimary)
    {
        var config = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: localIsPrimary),
                new BackupDestination("sftp", Guid.NewGuid(), IsPrimary: sftpIsPrimary)
            ]
        };

        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void MultiDestination_RejectsDuplicates()
    {
        var conn = Guid.NewGuid();
        var duplicate = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("sftp", conn, IsPrimary: true),
                new BackupDestination("sftp", conn, IsPrimary: false)
            ]
        };
        Assert.Throws<ArgumentException>(() => duplicate.Validate());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)] // more than the two destinations
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void MultiDestination_ValidatesRequiredVerifiedReplicaCountBounds(int required, bool valid)
    {
        var config = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true),
                new BackupDestination("sftp", Guid.NewGuid(), IsPrimary: false)
            ],
            RequiredVerifiedReplicaCount = required
        };

        if (valid)
            Assert.NotNull(config.Validate());
        else
            Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void MultiDestination_JsonRoundtripPreservesDestinationsAndReplicaCount()
    {
        var sftpConn = Guid.NewGuid();
        var original = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true),
                new BackupDestination("sftp", sftpConn, IsPrimary: false)
            ],
            RequiredVerifiedReplicaCount = 2
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<BackupConfiguration>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Destinations.Count);
        Assert.Equal(2, deserialized.RequiredVerifiedReplicaCount);
        Assert.Equal("local", deserialized.Destinations[0].StorageProvider);
        Assert.True(deserialized.Destinations[0].IsPrimary);
        Assert.Equal(sftpConn, deserialized.Destinations[1].StorageConnectionId);
        Assert.False(deserialized.Destinations[1].IsPrimary);
    }

    [Fact]
    public void ApiContract_ToWireAndToDomain_PreserveMultiDestination()
    {
        var gdriveConn = Guid.NewGuid();
        var domain = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("central", null, IsPrimary: true),
                new BackupDestination("google-drive", gdriveConn, IsPrimary: false)
            ],
            RequiredVerifiedReplicaCount = 2
        };

        var wire = BackupOrchestrator.ToWire(domain);
        Assert.Equal(2, wire.Destinations.Count);
        Assert.Equal(2, wire.RequiredVerifiedReplicaCount);
        Assert.Equal("central", wire.Destinations[0].StorageProvider);
        Assert.True(wire.Destinations[0].IsPrimary);

        // Validation through BackupInputValidation
        var request = new CreateBackupJobRequest("Job 1", Guid.NewGuid(), wire);
        BackupInputValidation.Validate(request);
    }

    [Fact]
    public void BackupInputValidation_RejectsInvalidReplicaCountOrMissingPrimary()
    {
        var wire = new BackupExecutionConfiguration
        {
            Engine = "mssql", Database = "DB", Host = "host", IntegratedSecurity = true,
            Destinations =
            [
                new BackupDestinationContract { StorageProvider = "local", StorageExecutionLocation = "Agent", IsPrimary = false },
                new BackupDestinationContract { StorageProvider = "central", StorageExecutionLocation = "Backend", IsPrimary = false }
            ],
            RequiredVerifiedReplicaCount = 1
        };

        // Missing primary
        Assert.Throws<InvalidBackupJobException>(() =>
            BackupInputValidation.Validate(new CreateBackupJobRequest("Job", Guid.NewGuid(), wire)));

        // Required count > destinations count
        var wire2 = wire with
        {
            Destinations =
            [
                new BackupDestinationContract { StorageProvider = "local", StorageExecutionLocation = "Agent", IsPrimary = true }
            ],
            RequiredVerifiedReplicaCount = 2
        };
        Assert.Throws<InvalidBackupJobException>(() =>
            BackupInputValidation.Validate(new CreateBackupJobRequest("Job", Guid.NewGuid(), wire2)));
    }

    [Fact]
    public void LegacyJsonPayloadWithoutDestinations_DeserializesEmptyAndIsInvalid()
    {
        var legacyJson = """
        {
            "Engine": "mssql",
            "Database": "OldDB",
            "Host": "localhost",
            "IntegratedSecurity": true,
            "StagingReference": "default",
            "StorageProvider": "central",
            "StoragePrefix": "",
            "CopyOnly": true,
            "ChecksumPolicy": "Preferred",
            "CompressionPolicy": "Preferred"
        }
        """;

        var deserialized = JsonSerializer.Deserialize<BackupConfiguration>(legacyJson);
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Destinations);
        Assert.Throws<ArgumentException>(() => deserialized.Validate());
    }

    public static TheoryData<BackupExecutionConfiguration, string> InvalidDestinationShapes => new()
    {
        {
            new BackupExecutionConfiguration { Engine = "mssql", Database = "DB", Host = "host", IntegratedSecurity = true },
            "configuration.destinations"
        },
        {
            new BackupExecutionConfiguration
            {
                Engine = "mssql", Database = "DB", Host = "host", IntegratedSecurity = true,
                Destinations = [new BackupDestinationContract { StorageProvider = "central", IsPrimary = true }]
            },
            "configuration.destinations[0].storageExecutionLocation"
        }
    };

    [Theory]
    [MemberData(nameof(InvalidDestinationShapes))]
    public void JobRequestWithMissingDestinationsOrExecutionLocation_IsRejectedOnTheOffendingField(BackupExecutionConfiguration wire, string field)
    {
        var error = Assert.Throws<InvalidBackupJobException>(() =>
            BackupInputValidation.Validate(new CreateBackupJobRequest("Job", Guid.NewGuid(), wire)));
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void ToWire_ProjectsDestinationsOnly()
    {
        var destinations = new List<BackupDestination>
        {
            new("sftp", Guid.NewGuid(), IsPrimary: false),
            new("central", Guid.NewGuid(), IsPrimary: true)
        };
        var config = (CreateLegacy() with { Destinations = destinations, RequiredVerifiedReplicaCount = 2 }).Validate();

        var wire = BackupOrchestrator.ToWire(config);
        Assert.Equal(2, wire.Destinations.Count);
        Assert.Equal(2, wire.RequiredVerifiedReplicaCount);
        Assert.True(wire.Destinations[1].IsPrimary);
        Assert.Equal("Backend", wire.Destinations[1].StorageExecutionLocation);
    }
}
