using BackupPlatform.Agent.Host.Storage;

namespace BackupPlatform.AgentTests;

public sealed class SftpCapacityDfFallbackTests
{
    [Fact]
    public void Parses_posix_df_output_in_kibibyte_blocks()
    {
        var (total, free) = SftpCapacityProbeExecutor.ParseDf(
            "Filesystem     1024-blocks      Used Available Capacity Mounted on\n/dev/sda1         102400     40960     61440      40% /srv/backups\n");

        Assert.Equal(102_400L * 1024, total);
        Assert.Equal(61_440L * 1024, free);
    }

    [Fact]
    public void Parses_a_filesystem_and_mount_point_that_contain_spaces()
    {
        var (total, free) = SftpCapacityProbeExecutor.ParseDf(
            "Filesystem 1024-blocks Used Available Capacity Mounted on\nnas share one 2048 1024 1024 50% /mnt/my backups\n");

        Assert.Equal(2048L * 1024, total);
        Assert.Equal(1024L * 1024, free);
    }

    [Theory]
    [InlineData("")]
    [InlineData("df: /nope: No such file or directory")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/x - - - - /m\n")]
    public void Rejects_output_that_is_not_a_df_line(string output) =>
        Assert.Throws<IOException>(() => SftpCapacityProbeExecutor.ParseDf(output));
}
