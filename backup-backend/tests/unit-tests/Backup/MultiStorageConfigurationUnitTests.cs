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
    public void ConfigurationWithoutDestinations_IsInvalid()
    {
        Assert.Throws<ArgumentException>(() => CreateLegacy().Validate());
    }

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

    [Fact]
    public void MultiDestination_RequiresExactlyOnePrimary()
    {
        var sftpConn = Guid.NewGuid();

        // No primary
        var noPrimary = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: false),
                new BackupDestination("sftp", sftpConn, IsPrimary: false)
            ]
        };
        Assert.Throws<ArgumentException>(() => noPrimary.Validate());

        // Multiple primaries
        var multiPrimary = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true),
                new BackupDestination("sftp", sftpConn, IsPrimary: true)
            ]
        };
        Assert.Throws<ArgumentException>(() => multiPrimary.Validate());
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

    [Fact]
    public void MultiDestination_ValidatesRequiredVerifiedReplicaCountBounds()
    {
        var sftpConn = Guid.NewGuid();
        var config = CreateLegacy() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true),
                new BackupDestination("sftp", sftpConn, IsPrimary: false)
            ]
        };

        // 0 is invalid
        Assert.Throws<ArgumentException>(() => (config with { RequiredVerifiedReplicaCount = 0 }).Validate());

        // > count (3 > 2) is invalid
        Assert.Throws<ArgumentException>(() => (config with { RequiredVerifiedReplicaCount = 3 }).Validate());

        // 1 and 2 are valid
        Assert.NotNull((config with { RequiredVerifiedReplicaCount = 1 }).Validate());
        Assert.NotNull((config with { RequiredVerifiedReplicaCount = 2 }).Validate());
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

    [Fact]
    public void JobRequestWithoutDestinations_IsRejectedOnTheDestinationsField()
    {
        var wire = new BackupExecutionConfiguration { Engine = "mssql", Database = "DB", Host = "host", IntegratedSecurity = true };

        var error = Assert.Throws<InvalidBackupJobException>(() =>
            BackupInputValidation.Validate(new CreateBackupJobRequest("Job", Guid.NewGuid(), wire)));
        Assert.Equal("configuration.destinations", error.Field);
    }

    [Fact]
    public void DestinationWithoutExecutionLocation_IsRejected()
    {
        var wire = new BackupExecutionConfiguration
        {
            Engine = "mssql", Database = "DB", Host = "host", IntegratedSecurity = true,
            Destinations = [new BackupDestinationContract { StorageProvider = "central", IsPrimary = true }]
        };

        var error = Assert.Throws<InvalidBackupJobException>(() =>
            BackupInputValidation.Validate(new CreateBackupJobRequest("Job", Guid.NewGuid(), wire)));
        Assert.Equal("configuration.destinations[0].storageExecutionLocation", error.Field);
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
