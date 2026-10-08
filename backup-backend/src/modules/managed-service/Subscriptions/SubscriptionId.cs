namespace BackupPlatform.ManagedService.Subscriptions;

/// <summary>A nonempty subscription identity.</summary>
public sealed record SubscriptionId
{
    public SubscriptionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identity must not be empty.", nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }

    public static SubscriptionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
