using System.Data.Common;

namespace BackupPlatform.Infrastructure.Persistence;

public static class PlatformConnectionString
{
    public static string WithoutRemovedMultiplexingOptions(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder.Remove("Multiplexing");
        builder.Remove("WriteCoalescingBufferThresholdBytes");
        builder.Remove("Write Coalescing Buffer Threshold Bytes");
        return builder.ConnectionString;
    }
}
