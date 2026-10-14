using System.Net.Mail;
using System.Text.RegularExpressions;
using BackupPlatform.Domain.Notifications;

namespace BackupPlatform.Notifications;

/// <summary>Per-kind validation and display of a channel's target. A target is stored whole but only ever shown masked, because a webhook path or a phone number is itself sensitive.</summary>
public static partial class NotificationTargets
{
    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$")]
    private static partial Regex E164();

    /// <summary>The normalized target, or null when <paramref name="input"/> is not a valid target of <paramref name="kind"/>.</summary>
    public static string? Normalize(NotificationChannelKind kind, string? input)
    {
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > NotificationChannel.MaximumTargetLength || value.Any(char.IsControl)) return null;
        return kind switch
        {
            NotificationChannelKind.Email => NormalizeEmail(value),
            NotificationChannelKind.Sms => E164().IsMatch(value) ? value : null,
            NotificationChannelKind.Webhook => NormalizeWebhook(value),
            _ => null
        };
    }

    public static string Display(NotificationChannelKind kind, string target) => kind switch
    {
        NotificationChannelKind.Email => target,
        NotificationChannelKind.Sms => target.Length <= 5 ? "+••" : string.Concat(target.AsSpan(0, 3), new string('•', target.Length - 5), target.AsSpan(target.Length - 2)),
        _ => Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) + (uri.AbsolutePath.Length > 1 ? "/…" : string.Empty) : "https://…"
    };

    private static string? NormalizeEmail(string value)
    {
        if (value.Length > 254 || value.Any(char.IsWhiteSpace) || value.IndexOfAny(['<', '>', '"', ',', ';']) >= 0) return null;
        if (!MailAddress.TryCreate(value, out var address) || address.Address != value || !address.Host.Contains('.')) return null;
        return address.Address;
    }

    private static string? NormalizeWebhook(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown) return null;
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath;
    }
}
