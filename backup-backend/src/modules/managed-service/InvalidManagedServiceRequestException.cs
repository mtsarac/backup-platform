namespace BackupPlatform.ManagedService;

/// <summary>Machine-readable validation failure naming the offending field; never carries the rejected value.</summary>
public sealed class InvalidManagedServiceRequestException(string field, string code)
    : Exception($"Invalid managed service request field '{field}' ({code}).")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}
