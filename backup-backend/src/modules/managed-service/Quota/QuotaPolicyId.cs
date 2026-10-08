namespace BackupPlatform.ManagedService.Quota;

/// <summary>A nonempty quota policy identity.</summary>
public sealed record QuotaPolicyId
{
    public QuotaPolicyId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identity must not be empty.", nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }

    public static QuotaPolicyId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
