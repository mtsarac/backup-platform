using System.Globalization;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Notifications;
using Microsoft.AspNetCore.DataProtection;

namespace BackupPlatform.Api.Notifications;

/// <summary>
/// Recipient link tokens sealed with the platform's Data Protection keys (shared by every node), so a link minted by one node is honoured by any other
/// and nothing is stored. A confirmation link is short-lived; an unsubscribe link stays valid for a year, because someone digging an old alert out of
/// their mailbox must still be able to stop it.
/// </summary>
public sealed class DataProtectionRecipientTokens(IDataProtectionProvider protection, TimeProvider time) : IRecipientTokens
{
    private static readonly TimeSpan ConfirmLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnsubscribeLifetime = TimeSpan.FromDays(365);

    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("BackupPlatform.Notifications.RecipientLink.v1").ToTimeLimitedDataProtector();

    public string Issue(RecipientClaim claim) =>
        _protector.Protect($"{claim.TenantId.Value:N}|{claim.ChannelId:N}|{(int)claim.Action}|{claim.TargetFingerprint}",
            claim.Action == RecipientAction.Confirm ? ConfirmLifetime : UnsubscribeLifetime);

    public RecipientClaim? Read(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512) return null;
        try
        {
            var parts = _protector.Unprotect(token, out var expiration).Split('|');
            if (expiration <= time.GetUtcNow() || parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out var tenant) || !Guid.TryParseExact(parts[1], "N", out var channel)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var action) || !Enum.IsDefined((RecipientAction)action))
                return null;
            return new RecipientClaim(new TenantId(tenant), channel, (RecipientAction)action, parts[3]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
