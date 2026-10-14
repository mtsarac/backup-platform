using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;

namespace BackupPlatform.Notifications;

public enum RecipientAction
{
    /// <summary>The address's owner agrees to receive this tenant's alert emails.</summary>
    Confirm = 1,
    /// <summary>The address's owner refuses further emails.</summary>
    Unsubscribe = 2
}

/// <summary>What a recipient link is allowed to do: one action on one channel of one tenant, bound to the address it was mailed to.</summary>
public sealed record RecipientClaim(TenantId TenantId, Guid ChannelId, RecipientAction Action, string TargetFingerprint);

/// <summary>
/// Issues and reads the tokens inside confirmation and unsubscribe links. A token is unforgeable and tamper-evident, expires on its own and is never
/// stored, so it grants exactly its claim and nothing else. Implemented by the host (Data Protection keys are shared across nodes).
/// </summary>
public interface IRecipientTokens
{
    string Issue(RecipientClaim claim);

    /// <summary>The claim of a genuine, unexpired token; null for anything else.</summary>
    RecipientClaim? Read(string? token);
}

public static class RecipientLinks
{
    /// <summary>Ties a token to the address it was mailed to: after the channel is re-pointed, a link sent to the previous owner no longer works.</summary>
    public static string Fingerprint(string target) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToLowerInvariant())))[..16];

    public static string Url(NotificationOptions options, string token) =>
        $"{options.PublicBaseUrl!.TrimEnd('/')}/api/notifications/recipient?token={Uri.EscapeDataString(token)}";
}

public enum RecipientOutcome
{
    Invalid,
    Confirmed,
    Unsubscribed,
    /// <summary>The channel was deleted or re-pointed to another address since the link was sent.</summary>
    Gone
}

/// <summary>
/// The anonymous half of email recipient consent: the owner of an address confirms it (so a tenant administrator cannot aim the platform's mail relay at
/// strangers) or unsubscribes it. The link's token carries the tenant and channel, so every change goes through the same tenant-serialized, audited
/// channel mutation as an administrator's. Repeating a link is harmless.
/// </summary>
public sealed class NotificationRecipientService(INotificationStore store, IRecipientTokens tokens, TimeProvider time)
{
    private static readonly NotificationActor Recipient = new(UserId.System, AuditActorKind.Anonymous);

    /// <summary>The claim of a valid token, to show what the link would do before anything changes.</summary>
    public RecipientClaim? Inspect(string? token) => tokens.Read(token);

    public async Task<RecipientOutcome> ApplyAsync(string? token, CancellationToken cancellationToken)
    {
        if (tokens.Read(token) is not { } claim) return RecipientOutcome.Invalid;
        var now = time.GetUtcNow();
        var stale = false;
        var result = await store.MutateChannelAsync(claim.TenantId, claim.ChannelId, Recipient, channel =>
        {
            if (channel.Kind != NotificationChannelKind.Email || RecipientLinks.Fingerprint(channel.NormalizedTarget) != claim.TargetFingerprint)
            {
                stale = true;
                return null;
            }
            return claim.Action == RecipientAction.Confirm
                ? channel.ConfirmRecipient(now) ? new ChannelChange(NotificationAuditActions.ChannelRecipientConfirmed) : null
                : channel.Unsubscribe(now) ? new ChannelChange(NotificationAuditActions.ChannelRecipientUnsubscribed) : null;
        }, cancellationToken);
        if (stale || result.Outcome is NotificationOutcome.NotFound) return RecipientOutcome.Gone;
        if (result.Outcome is not (NotificationOutcome.Changed or NotificationOutcome.Unchanged)) return RecipientOutcome.Invalid;
        return claim.Action == RecipientAction.Confirm ? RecipientOutcome.Confirmed : RecipientOutcome.Unsubscribed;
    }
}
