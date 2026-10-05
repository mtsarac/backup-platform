namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>
/// A secret value that never renders itself. <see cref="ToString"/> is redacted, there is no value equality and no
/// serialization surface, so logging or formatting a credential cannot leak it. The only way to the text is the explicit
/// <see cref="Reveal"/> call made by the provider at the moment it authenticates.
/// </summary>
public sealed class DatabaseSecret
{
    private readonly string _value;

    public DatabaseSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public string Reveal() => _value;

    public override string ToString() => "[redacted]";
}

/// <summary>
/// Credential material, deliberately separate from <see cref="DatabaseConnectionSettings"/>. The caller resolves it (from a
/// secret provider, an operating-system store, ...) and hands it to the provider per call, so persistence stays outside
/// the provider.
/// </summary>
public abstract class DatabaseCredential
{
    private protected DatabaseCredential()
    {
    }
}

/// <summary>Authenticate as the identity of the agent process (Windows Authentication, or Kerberos where the platform supports it).</summary>
public sealed class IntegratedDatabaseCredential : DatabaseCredential
{
    public static IntegratedDatabaseCredential Instance { get; } = new();

    private IntegratedDatabaseCredential()
    {
    }

    public override string ToString() => "Integrated";
}

/// <summary>A user name and password (SQL Server Authentication).</summary>
public sealed class PasswordDatabaseCredential : DatabaseCredential
{
    public PasswordDatabaseCredential(string userName, DatabaseSecret password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        UserName = userName;
        Password = password;
    }

    public string UserName { get; }
    public DatabaseSecret Password { get; }

    public override string ToString() => $"Password({UserName})";
}
