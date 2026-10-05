using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerBackupOptionResolverTests
{
    public enum Option
    {
        CopyOnly,
        Checksum,
        Compression
    }

    private static DatabaseCapabilitySet Capabilities(DatabaseCapabilitySupport copyOnly, DatabaseCapabilitySupport checksum, DatabaseCapabilitySupport compression) =>
        new(
        [
            new DatabaseCapability(SqlServerCapabilityIds.CopyOnly, copyOnly, "r", "e"),
            new DatabaseCapability(SqlServerCapabilityIds.BackupChecksum, checksum, "r", "e"),
            new DatabaseCapability(SqlServerCapabilityIds.BackupCompression, compression, "r", "e")
        ]);

    private static DatabaseCapabilitySet Only(Option option, DatabaseCapabilitySupport support)
    {
        var other = DatabaseCapabilitySupport.Supported;
        return option switch
        {
            Option.CopyOnly => Capabilities(support, other, other),
            Option.Checksum => Capabilities(other, support, other),
            _ => Capabilities(other, other, support)
        };
    }

    private static FullBackupOptions Request(Option option, BackupOptionMode mode) => option switch
    {
        Option.CopyOnly => new FullBackupOptions(copyOnly: mode),
        Option.Checksum => new FullBackupOptions(checksum: mode),
        _ => new FullBackupOptions(compression: mode)
    };

    private static bool Applied(AppliedFullBackupOptions applied, Option option) => option switch
    {
        Option.CopyOnly => applied.CopyOnly,
        Option.Checksum => applied.Checksum,
        _ => applied.Compression
    };

    [Theory]
    [InlineData(BackupOptionMode.Off, DatabaseCapabilitySupport.Supported, false, null)]
    [InlineData(BackupOptionMode.Off, DatabaseCapabilitySupport.Unsupported, false, null)]
    [InlineData(BackupOptionMode.Off, DatabaseCapabilitySupport.Unknown, false, null)]
    [InlineData(BackupOptionMode.IfSupported, DatabaseCapabilitySupport.Supported, true, null)]
    [InlineData(BackupOptionMode.Required, DatabaseCapabilitySupport.Supported, true, null)]
    [InlineData(BackupOptionMode.IfSupported, DatabaseCapabilitySupport.Unsupported, false, "unsupported-fallback")]
    [InlineData(BackupOptionMode.IfSupported, DatabaseCapabilitySupport.Unknown, false, "support-unknown-skipped")]
    [InlineData(BackupOptionMode.Required, DatabaseCapabilitySupport.Unknown, true, "support-unknown-applied")]
    public void EachModeAndSupportLevelHasOneOutcomeAndAWarningWhereTheOptionDidNotBehaveAsAsked(
        BackupOptionMode mode, DatabaseCapabilitySupport support, bool applied, string? warningSuffix)
    {
        foreach (var option in Enum.GetValues<Option>())
        {
            var resolution = SqlServerBackupOptionResolver.Resolve(Request(option, mode), Only(option, support));

            Assert.Equal(applied, Applied(resolution.Outcome.Applied, option));
            Assert.Empty(resolution.UnsupportedRequired);
            var expectedKey = option switch { Option.CopyOnly => "copy-only", Option.Checksum => "checksum", _ => "compression" };
            if (warningSuffix is null)
            {
                Assert.Empty(resolution.Warnings);
            }
            else
            {
                Assert.Equal($"{expectedKey}-{warningSuffix}", Assert.Single(resolution.Warnings).Code);
            }
        }
    }

    [Fact]
    public void RequiredButUnsupportedIsNotAppliedAndIsReportedForTheCallerToRefuse()
    {
        var resolution = SqlServerBackupOptionResolver.Resolve(
            new FullBackupOptions(BackupOptionMode.Required, BackupOptionMode.Required, BackupOptionMode.Required),
            Capabilities(DatabaseCapabilitySupport.Unsupported, DatabaseCapabilitySupport.Supported, DatabaseCapabilitySupport.Unsupported));

        Assert.Equal(["copy-only", "compression"], resolution.UnsupportedRequired);
        Assert.Equal(new AppliedFullBackupOptions(false, true, false), resolution.Outcome.Applied);
    }

    [Fact]
    public void OutcomeKeepsWhatWasRequestedNextToWhatWasApplied()
    {
        var requested = new FullBackupOptions(BackupOptionMode.IfSupported, BackupOptionMode.Required, BackupOptionMode.IfSupported);

        var resolution = SqlServerBackupOptionResolver.Resolve(
            requested, Capabilities(DatabaseCapabilitySupport.Supported, DatabaseCapabilitySupport.Supported, DatabaseCapabilitySupport.Unsupported));

        Assert.Same(requested, resolution.Outcome.Requested);
        Assert.Equal(new AppliedFullBackupOptions(true, true, false), resolution.Outcome.Applied);
        Assert.False(resolution.CompressionCapabilitySupported);
    }

    [Fact]
    public void ACapabilityThatWasNeverEvaluatedCountsAsUnknownNeverAsSupported()
    {
        var resolution = SqlServerBackupOptionResolver.Resolve(
            new FullBackupOptions(compression: BackupOptionMode.IfSupported), new DatabaseCapabilitySet([]));

        Assert.False(resolution.Outcome.Applied.Compression);
        Assert.Equal(SqlServerBackupWarningCodes.CompressionSupportUnknownSkipped, Assert.Single(resolution.Warnings).Code);
        Assert.False(resolution.CompressionCapabilitySupported);
    }
}
