namespace BackupPlatform.Domain.Identity;

/// <summary>A nonempty identity. Default reference values are null and rejected at domain boundaries.</summary>
public sealed record UserId
{
    public UserId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identity must not be empty.", nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }

    public static UserId New() => new(Guid.NewGuid());

    /// <summary>Well-known identity used when background workers or the platform engine act without a human user.</summary>
    public static readonly UserId System = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    /// <summary>Audit sentinel for unauthenticated requests; never an authenticated account.</summary>
    public static readonly UserId Anonymous = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    public override string ToString() => Value.ToString("D");
}
