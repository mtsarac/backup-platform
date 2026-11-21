namespace BackupPlatform.IntegrationTests;

internal static class ListAssertions
{
    /// <summary>Each inner set is a group of rows whose relative order is a tie (broken by id), so it is compared as a set.</summary>
    public static void Groups(string[] actual, string[][] groups)
    {
        var index = 0;
        foreach (var group in groups)
        {
            Assert.Equal(group.Order(), actual.Skip(index).Take(group.Length).Order());
            index += group.Length;
        }
        Assert.Equal(index, actual.Length);
    }
}
