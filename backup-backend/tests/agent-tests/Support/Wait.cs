namespace BackupPlatform.AgentTests.Support;

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {description}");
            await Task.Delay(20);
        }
    }
}
