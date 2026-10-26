using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Notifications;

/// <summary>
/// One place a tenant's notifications go: an email recipient, a webhook endpoint or an SMS recipient. <see cref="Target"/> is the address
/// (validated per kind by the notifications module before it gets here) and is never returned by an API; only a masked display form is.
/// Credentials live in the secret store; the row holds a reference only.
/// </summary>
public sealed class NotificationChannel : ITenantOwned
{
    public const int MaximumNameLength = 100;
    public const int MaximumTargetLength = 512;

    private NotificationChannel()
    {
        TenantId = null!;
        Name = null!;
        NormalizedName = null!;
        Target = null!;
        NormalizedTarget = null!;
    }

    public NotificationChannel(
        Guid id, TenantId tenantId, NotificationChannelKind kind, string name, string target, WebhookAuthentication authentication, DateTimeOffset now)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown channel kind.", nameof(kind));
        Id = id;
        TenantId = tenantId;
        Kind = kind;
        (Name, NormalizedName) = Names(name);
        (Target, NormalizedTarget) = Targets(kind, target);
        Authentication = SanitizeAuthentication(kind, authentication);
        Enabled = true;
        CreatedAt = BackupGuard.Timestamp(now);
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    /// <summary>Immutable: a channel is one kind of destination for its whole life.</summary>
    public NotificationChannelKind Kind { get; private set; }
    public string Name { get; private set; }
    /// <summary>Case-insensitive uniqueness key within the tenant.</summary>
    public string NormalizedName { get; private set; }
    public string Target { get; private set; }
    /// <summary>Case-insensitive uniqueness key of the destination within (tenant, kind).</summary>
    public string NormalizedTarget { get; private set; }
    public WebhookAuthentication Authentication { get; private set; }
    public bool Enabled { get; private set; }
    /// <summary>The name of the secret (webhook bearer token or signing key) in the secret provider; not secret material.</summary>
    public string? SecretReference { get; private set; }
    public DateTimeOffset? SecretUpdatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool HasSecret => SecretReference is not null;

    /// <summary>Email only: when the owner of the address confirmed it wants this tenant's alerts. Null until then; webhook and SMS channels never have one.</summary>
    public DateTimeOffset? RecipientVerifiedAt { get; private set; }
    /// <summary>
    /// Email only: when the owner of the address refused further mail. While set, no earlier confirmation link can verify the address again; turning the channel back on
    /// (which sends a new invitation) or pointing it at another address clears it.
    /// </summary>
    public DateTimeOffset? UnsubscribedAt { get; private set; }
    /// <summary>An Owner explicitly designated this confirmed email for mandatory tenant security notices; never inferred from a general recipient.</summary>
    public UserId? SecurityOwnerUserId { get; private set; }

    public bool SetSecurityOwner(UserId? owner, DateTimeOffset now)
    {
        if (owner is not null && (Kind != NotificationChannelKind.Email || !IsDeliverable))
            throw new ArgumentException("A security recipient must be an enabled, confirmed email channel.");
        if (SecurityOwnerUserId == owner) return false;
        SecurityOwnerUserId = owner;
        Touch(now);
        return true;
    }

    /// <summary>Alerts may be sent: enabled, and for email a confirmed recipient (an unconfirmed address could be anyone's inbox).</summary>
    public bool IsDeliverable => Enabled && (Kind != NotificationChannelKind.Email || RecipientVerifiedAt is not null);

    /// <summary>Email channels wait for the recipient's confirmation; the other kinds have none.</summary>
    public bool AwaitsConfirmation => Kind == NotificationChannelKind.Email && RecipientVerifiedAt is null;

    public static (string Display, string Normalized) Names(string? name)
    {
        var display = name?.Trim();
        if (string.IsNullOrEmpty(display) || display.Length > MaximumNameLength || display.Any(char.IsControl))
            throw new ArgumentException("Invalid notification channel name.", nameof(name));
        return (display, display.ToLowerInvariant());
    }

    private static (string Target, string Normalized) Targets(NotificationChannelKind kind, string? target)
    {
        var value = target?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaximumTargetLength || value.Any(char.IsControl))
            throw new ArgumentException("Invalid notification target.", nameof(target));
        // Hosts and addresses are case-insensitive for our purposes; a webhook path is not, so only its authority is folded by the module.
        return (value, kind == NotificationChannelKind.Webhook ? value : value.ToLowerInvariant());
    }

    private static WebhookAuthentication SanitizeAuthentication(NotificationChannelKind kind, WebhookAuthentication authentication)
    {
        if (!Enum.IsDefined(authentication)) throw new ArgumentException("Unknown authentication.", nameof(authentication));
        return kind == NotificationChannelKind.Webhook ? authentication : WebhookAuthentication.None;
    }

    public bool Rename(string name, DateTimeOffset now)
    {
        var (display, normalized) = Names(name);
        if (Name == display) return false;
        (Name, NormalizedName) = (display, normalized);
        Touch(now);
        return true;
    }

    public bool Retarget(string target, DateTimeOffset now)
    {
        var (value, normalized) = Targets(Kind, target);
        if (Target == value) return false;
        (Target, NormalizedTarget) = (value, normalized);
        // A new address has not agreed to receive anything yet.
        RecipientVerifiedAt = null;
        UnsubscribedAt = null;
        SecurityOwnerUserId = null;
        Touch(now);
        return true;
    }

    /// <summary>The address's owner followed the confirmation link. Only email recipients are confirmed, and not after they unsubscribed; false when nothing changed.</summary>
    public bool ConfirmRecipient(DateTimeOffset now)
    {
        if (Kind != NotificationChannelKind.Email || RecipientVerifiedAt is not null || UnsubscribedAt is not null) return false;
        RecipientVerifiedAt = BackupGuard.Timestamp(now);
        Touch(now);
        return true;
    }

    /// <summary>The address's owner refused further mail: the channel stops and must be confirmed again before it can send. False when nothing changed.</summary>
    public bool Unsubscribe(DateTimeOffset now)
    {
        if (Kind != NotificationChannelKind.Email || UnsubscribedAt is not null) return false;
        Enabled = false;
        UnsubscribedAt = BackupGuard.Timestamp(now);
        RecipientVerifiedAt = null;
        SecurityOwnerUserId = null;
        Touch(now);
        return true;
    }

    /// <summary>
    /// Switches the webhook authentication. Returns the secret reference the caller must delete after commit when the switch made the stored secret
    /// meaningless (to <see cref="WebhookAuthentication.None"/>), and null otherwise. <paramref name="changed"/> is false when nothing changed.
    /// </summary>
    public string? ChangeAuthentication(WebhookAuthentication authentication, DateTimeOffset now, out bool changed)
    {
        if (Kind != NotificationChannelKind.Webhook && authentication != WebhookAuthentication.None)
            throw new ArgumentException("Only webhooks authenticate.", nameof(authentication));
        if (!Enum.IsDefined(authentication)) throw new ArgumentException("Unknown authentication.", nameof(authentication));
        changed = Authentication != authentication;
        if (!changed) return null;
        Authentication = authentication;
        var previous = authentication == WebhookAuthentication.None ? DetachSecretCore() : null;
        Touch(now);
        return previous;
    }

    public bool SetEnabled(bool enabled, DateTimeOffset now)
    {
        if (Enabled == enabled) return false;
        Enabled = enabled;
        if (enabled) UnsubscribedAt = null;
        Touch(now);
        return true;
    }

    /// <summary>Points the channel at a (new) secret and returns the reference it replaced so the caller can delete it after commit.</summary>
    public string? AttachSecret(string reference, DateTimeOffset now)
    {
        BackupGuard.Reference(reference);
        if (Authentication == WebhookAuthentication.None) throw new InvalidOperationException("The channel does not use a secret.");
        var previous = SecretReference;
        SecretReference = reference;
        SecretUpdatedAt = BackupGuard.Timestamp(now);
        Touch(now);
        return previous;
    }

    public string? DetachSecret(DateTimeOffset now)
    {
        var previous = DetachSecretCore();
        if (previous is not null) Touch(now);
        return previous;
    }

    private string? DetachSecretCore()
    {
        var previous = SecretReference;
        SecretReference = null;
        SecretUpdatedAt = null;
        return previous;
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = BackupGuard.Timestamp(now);
}
