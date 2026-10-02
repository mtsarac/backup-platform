namespace BackupPlatform.Domain.Identity;

/// <summary>A nonempty identity. Default reference values are null and rejected at domain boundaries.</summary>
public sealed record TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identity must not be empty.", nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }

    public static TenantId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
