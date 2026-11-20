using BackupPlatform.Domain;

namespace BackupPlatform.UnitTests.Lists;

public sealed class NameKeyTests
{
    [Theory]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("QA Kanalı", "qa kanalı")]
    [InlineData("Plan A", "plan a")]
    public void Of_LowerCasesLikePostgres(string name, string expected) => Assert.Equal(expected, NameKey.Of(name));
}
