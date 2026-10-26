using BackupPlatform.Domain.Backups;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class FileJobConfigurationTests
{
    private static BackupConfiguration Files(bool copyOnly, string checksum) => new("file-system", "src", "localhost", null, true, null, false, false, "default", "", copyOnly, checksum, "Preferred")
    {
        Files = new FileBackupPolicy(),
        Destinations = [new BackupDestination("local", null, true)]
    };

    [Fact]
    public void Copy_only_checksummed_file_job_is_valid() => Files(true, "Required").ValidateForSave();

    [Theory]
    [InlineData(false, "Required")]
    [InlineData(true, "Disabled")]
    public void File_job_options_the_engine_cannot_honor_are_rejected_at_creation(bool copyOnly, string checksum) =>
        Assert.Throws<ArgumentException>(() => Files(copyOnly, checksum).ValidateForSave());

    [Fact]
    public void A_stored_job_saved_before_the_rule_still_loads() => Files(false, "Disabled").Validate();
}
