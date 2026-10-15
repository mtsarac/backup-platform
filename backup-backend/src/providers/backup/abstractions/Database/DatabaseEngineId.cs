namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>
/// Identifies a database engine family (for example <c>mssql</c>), never a runtime: a native Windows service, a native
/// Linux service and a container all expose the same engine and are reached through the same provider.
/// </summary>
public sealed record DatabaseEngineId
{
    public DatabaseEngineId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || !value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("Engine identifiers contain only ASCII lowercase letters, digits and hyphens.", nameof(value));
        }

        Value = value;
    }

    public static DatabaseEngineId MsSql { get; } = new("mssql");
    public static DatabaseEngineId PostgreSql { get; } = new("postgres");

    public string Value { get; }

    public override string ToString() => Value;
}
