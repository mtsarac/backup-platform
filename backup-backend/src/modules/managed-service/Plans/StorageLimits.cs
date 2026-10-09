namespace BackupPlatform.ManagedService.Plans;

/// <summary>
/// The one definition of the commercial storage unit. Capacity is stored and exchanged as bytes; a commercial "GB" is
/// decimal (1 GB = 1,000,000,000 bytes), never binary.
/// </summary>
public static class StorageLimits
{
    public const long BytesPerGigabyte = 1_000_000_000;

    /// <summary>
    /// Largest accepted explicit limit (1,000,000 GB). Far above any plan, small enough that summing many usages against
    /// it cannot overflow a signed 64-bit counter and that every value stays exactly representable in a JSON number
    /// read by a JavaScript client (below 2^53).
    /// </summary>
    public const long MaximumBytes = 1_000_000_000_000_000;

    public static bool IsValid(long bytes) => bytes is > 0 and <= MaximumBytes;
}
