using System.Collections.ObjectModel;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Domain.Notifications;

/// <summary>Transport keys: the platform-operated senders (SMTP relay, SMS gateway). A webhook has none: its endpoint belongs to the tenant.</summary>
public static class NotificationTransports
{
    public const string Email = "email";
    public const string Sms = "sms";

    public static string? KeyOf(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Email => Email,
        NotificationChannelKind.Sms => Sms,
        _ => null
    };

    public static bool IsValid(string? key) => key is Email or Sms;
}

/// <summary>
/// The platform operator's configuration of one transport: a safe, non-secret dictionary (host, port, security, sender, endpoint...) and a
/// reference to the credential in the secret provider. One row per transport. Validation of the values is the notifications module's job (it
/// knows what each transport means); this entity only bounds and snapshots them.
/// </summary>
public sealed class NotificationTransportSettings
{
    public const int MaximumEntries = 16;
    public const int MaximumValueLength = 512;

    private static readonly IReadOnlyDictionary<string, string> NoConfiguration = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private NotificationTransportSettings()
    {
        Key = null!;
        Configuration = NoConfiguration;
    }

    public NotificationTransportSettings(string key, DateTimeOffset now)
    {
        if (!NotificationTransports.IsValid(key)) throw new ArgumentException("Unknown transport.", nameof(key));
        Key = key;
        Configuration = NoConfiguration;
        UpdatedAt = BackupGuard.Timestamp(now);
    }

    public string Key { get; private set; }
    public bool Enabled { get; private set; }
    public IReadOnlyDictionary<string, string> Configuration { get; private set; }
    public string? SecretReference { get; private set; }
    public DateTimeOffset? SecretUpdatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool HasSecret => SecretReference is not null;

    public bool Apply(bool enabled, IReadOnlyDictionary<string, string> configuration, DateTimeOffset now)
    {
        var next = Snapshot(configuration);
        if (Enabled == enabled && next.Count == Configuration.Count && next.All(pair => Configuration.TryGetValue(pair.Key, out var current) && current == pair.Value))
            return false;
        Enabled = enabled;
        Configuration = next;
        UpdatedAt = BackupGuard.Timestamp(now);
        return true;
    }

    public string? AttachSecret(string reference, DateTimeOffset now)
    {
        BackupGuard.Reference(reference);
        var previous = SecretReference;
        SecretReference = reference;
        SecretUpdatedAt = UpdatedAt = BackupGuard.Timestamp(now);
        return previous;
    }

    public string? DetachSecret(DateTimeOffset now)
    {
        var previous = SecretReference;
        if (previous is null) return null;
        SecretReference = null;
        SecretUpdatedAt = null;
        UpdatedAt = BackupGuard.Timestamp(now);
        return previous;
    }

    private static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string> configuration)
    {
        if (configuration.Count == 0) return NoConfiguration;
        if (configuration.Count > MaximumEntries) throw new ArgumentException("Too many configuration entries.", nameof(configuration));
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in configuration)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 32 || value is null || value.Length > MaximumValueLength || value.Any(char.IsControl))
                throw new ArgumentException("Invalid configuration entry.", nameof(configuration));
            copy[key] = value;
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}
