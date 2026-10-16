using System.Text.Json;
using BackupPlatform.Application.Backups;

namespace BackupPlatform.UnitTests;

public sealed class BackupMetadataProjectionTests
{
    [Fact]
    public void ProjectsPersistedBackupSqlVerificationAndAppliedOptionsWithoutUnknownData()
    {
        const string source = """
            {"engine":"mssql","databaseName":"Accounts","backupType":"Full",
             "backupStartedAt":"2026-10-06T10:00:00Z","backupFinishedAt":"2026-10-06T10:02:00Z",
             "instance":{"serverName":"sql-server","instanceName":"MAIN","version":"16.0.1000","edition":"Standard","hostPlatform":"Linux","password":"secret-instance-value"},
             "details":{"recovery-model":"full","compatibility-level":"160","transparent-data-encryption":"disabled","user-access":"multi-user","connection-string":"secret-detail-value"},
             "backupVerification":{"state":"Passed","method":"RestoreVerifyOnly","startedAt":"2026-10-06T10:02:00Z","finishedAt":"2026-10-06T10:03:00Z","serverPath":"secret-source-path"},
             "leaseToken":"secret-lease-token"}
            """;
        var view = BackupMetadataProjection.Read(source, "{\"copyOnly\":true,\"checksum\":true,\"compression\":false,\"credential\":\"secret-option-value\"}");
        Assert.NotNull(view);
        Assert.Equal("Accounts", view.DatabaseName);
        Assert.Equal("16.0.1000", view.Instance!.Version);
        Assert.Equal(160, view.SqlDetails!.CompatibilityLevel);
        Assert.Equal("full", view.SqlDetails.RecoveryModel);
        Assert.Equal("Passed", view.Verification!.State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T10:00:00Z"), view.BackupStartedAt);
        Assert.True(view.AppliedOptions!.CopyOnly);
        Assert.False(view.AppliedOptions.Compression);
        var json = JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
        Assert.DoesNotContain("secret-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("leaseToken", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-string", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlMetadataUsesAnAllowlistAndDoesNotExposeProviderSecrets()
    {
        var view = BackupMetadataProjection.Read("""
            {"engine":"postgres","databaseName":"sales","details":{"archive-format":"custom","client-version":"18.6","encoding":"UTF8","collation":"C",
             "data-checksums":"on","database-size-bytes":"16384","extensions":"plpgsql, hstore","password":"secret-password","remote-path":"secret-path"}}
            """, """{"copyOnly":false,"checksum":true,"compression":true}""");
        Assert.NotNull(view);
        Assert.Equal("custom", view.PostgreSqlDetails!.ArchiveFormat);
        Assert.Equal("18.6", view.PostgreSqlDetails.ClientVersion);
        Assert.Equal("on", view.PostgreSqlDetails.DataChecksums);
        Assert.Equal("plpgsql, hstore", view.PostgreSqlDetails.Extensions);
        Assert.DoesNotContain("secret-", JsonSerializer.Serialize(view, JsonSerializerOptions.Web), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", "mssql")]
    [InlineData("{\"databaseName\":\"legacy\"}", "mssql")]
    [InlineData("{\"engine\":\"postgres\"}", "postgres")]
    [InlineData("{\"Engine\":\"mssql\"}", "mssql")]
    [InlineData("{\"engine\":\"oracle\"}", null)]
    [InlineData("{\"engine\":null}", null)]
    [InlineData("{\"engine\":\"postgres\",\"Engine\":\"mssql\"}", null)]
    public void ArtifactRoutingDefaultsOnlyGenuinelyLegacyMetadata(string metadata, string? expected) =>
        Assert.Equal(expected, BackupPlatform.Domain.Backups.BackupArtifactEngine.Read(metadata));

    [Fact]
    public void MissingLegacyFactsRemainAbsentAndInvalidFieldTypesAreNotInvented()
    {
        Assert.Null(BackupMetadataProjection.Read("{}", "{}"));
        var view = BackupMetadataProjection.Read("{\"engine\":\"mssql\",\"instance\":{\"version\":42},\"details\":{\"compatibility-level\":\"unknown\"},\"backupStartedAt\":\"not-a-date\"}", "{\"copyOnly\":\"true\"}");
        Assert.NotNull(view);
        Assert.Null(view.Instance!.Version);
        Assert.Null(view.SqlDetails!.CompatibilityLevel);
        Assert.Null(view.BackupStartedAt);
        Assert.Null(view.AppliedOptions!.CopyOnly);
        Assert.Null(view.DatabaseName);
    }
}
