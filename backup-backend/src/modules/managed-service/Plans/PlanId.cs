namespace BackupPlatform.ManagedService.Plans;

/// <summary>A nonempty plan identity.</summary>
public sealed record PlanId
{
    public PlanId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identity must not be empty.", nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }

    public static PlanId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
