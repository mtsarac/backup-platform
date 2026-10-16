using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.StorageProviders.Local;

namespace BackupPlatform.AgentTests;

public sealed class LocalVolumesTests
{
    private static LocalStorageProvider Provider(string name) => new(new LocalStorageOptions(Path.Combine(Path.GetTempPath(), name)));

    [Theory]
    [InlineData("external-ssd", true)]
    [InlineData("local", false)]
    [InlineData("../x", false)]
    [InlineData("/mnt/ssd", false)]
    [InlineData("Upper", false)]
    [InlineData("-lead", false)]
    [InlineData("", false)]
    public void Only_a_plain_lowercase_name_other_than_the_default_is_a_volume_name(string name, bool valid) =>
        Assert.Equal(valid, LocalVolumes.IsValidName(name));

    [Fact]
    public void A_name_resolves_only_to_a_declared_volume_and_the_default_names_resolve_to_the_default_storage()
    {
        var defaultStorage = Provider("default");
        var ssd = Provider("ssd");
        var volumes = new LocalVolumes(defaultStorage, new Dictionary<string, BackupPlatform.StorageProviders.Abstractions.IStorageProvider> { ["external-ssd"] = ssd });

        Assert.Same(ssd, volumes.Resolve("external-ssd"));
        Assert.Same(defaultStorage, volumes.Resolve(null));
        Assert.Same(defaultStorage, volumes.Resolve("local"));
        Assert.Null(volumes.Resolve("unknown"));
        Assert.Null(LocalVolumes.None.Resolve("external-ssd"));
    }
}
