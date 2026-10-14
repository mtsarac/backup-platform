using System.Globalization;
using System.Text.RegularExpressions;
using BackupPlatform.Domain.Notifications;

namespace BackupPlatform.Notifications;

public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS, which is REQUIRED: a server that does not offer it is not used (no silent downgrade).</summary>
    StartTls,
    /// <summary>TLS from the first byte (SMTPS, usually port 465).</summary>
    Implicit,
    /// <summary>No encryption. Only without credentials (a trusted internal relay).</summary>
    None
}

public sealed record SmtpSettings(string Host, int Port, SmtpSecurity Security, string? Username, string FromAddress, string? FromName);

public enum SmsAuthentication { None, Bearer, Basic, Header }

public sealed record SmsSettings(string Provider, string Endpoint, SmsAuthentication Authentication, string? Username, string? HeaderName, string? Sender);

/// <summary>What each platform transport's configuration means: its keys, their validation, and when the transport counts as configured.</summary>
public static partial class NotificationTransportRules
{
    public const string GenericHttpProvider = "generic-http";

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderToken();

    [GeneratedRegex(@"^[A-Za-z0-9 +._-]{1,32}$")]
    private static partial Regex SenderText();

    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "transfer-encoding", "connection", "idempotency-key", "user-agent", "expect", "upgrade"
    };

    /// <summary>Parsed SMTP settings; null when the transport is disabled or incomplete (no host, no sender, credentials without a stored password).</summary>
    public static SmtpSettings? ReadEmail(NotificationTransportSettings? settings, bool requireEnabled = true)
    {
        if (settings is null || (requireEnabled && !settings.Enabled) || settings.Key != NotificationTransports.Email) return null;
        var c = settings.Configuration;
        if (!c.TryGetValue("host", out var host) || !c.TryGetValue("fromAddress", out var from) || !int.TryParse(c.GetValueOrDefault("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return null;
        if (!Enum.TryParse<SmtpSecurity>(c.GetValueOrDefault("security"), ignoreCase: true, out var security)) return null;
        var username = c.GetValueOrDefault("username");
        if (username is not null && !settings.HasSecret) return null;
        return new SmtpSettings(host, port, security, username, from, c.GetValueOrDefault("fromName"));
    }

    public static SmsSettings? ReadSms(NotificationTransportSettings? settings, bool requireEnabled = true)
    {
        if (settings is null || (requireEnabled && !settings.Enabled) || settings.Key != NotificationTransports.Sms) return null;
        var c = settings.Configuration;
        if (c.GetValueOrDefault("provider") != GenericHttpProvider || !c.TryGetValue("endpoint", out var endpoint)) return null;
        if (!Enum.TryParse<SmsAuthentication>(c.GetValueOrDefault("authentication") ?? "none", ignoreCase: true, out var authentication)) return null;
        var username = c.GetValueOrDefault("username");
        var header = c.GetValueOrDefault("headerName");
        var complete = authentication switch
        {
            SmsAuthentication.None => true,
            SmsAuthentication.Bearer => settings.HasSecret,
            SmsAuthentication.Basic => settings.HasSecret && username is not null,
            _ => settings.HasSecret && header is not null
        };
        return complete ? new SmsSettings(GenericHttpProvider, endpoint, authentication, username, header, c.GetValueOrDefault("sender")) : null;
    }

    /// <summary>The transport can be used right now: enabled and complete.</summary>
    public static bool IsAvailable(NotificationTransportSettings? settings) => ReadEmail(settings) is not null || ReadSms(settings) is not null;

    /// <summary>Enough is saved to attempt a send once the transport is enabled.</summary>
    public static bool IsConfigured(NotificationTransportSettings? settings) =>
        ReadEmail(settings, requireEnabled: false) is not null || ReadSms(settings, requireEnabled: false) is not null;

    /// <summary>Validates and normalizes the SMTP values of an administrator's request. Throws <see cref="InvalidNotificationException"/> naming the field.</summary>
    public static Dictionary<string, string> ValidateEmail(IReadOnlyDictionary<string, string?> values, bool enabled)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var host = Clean(values, "host");
        if (host is not null)
        {
            if (host.Length > 253 || host.Any(char.IsWhiteSpace) || host.Contains('/') || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown) throw new InvalidNotificationException("host", "invalid_value");
            result["host"] = host;
        }
        else if (enabled) throw new InvalidNotificationException("host", "required");
        var port = Clean(values, "port");
        if (port is not null)
        {
            if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535) throw new InvalidNotificationException("port", "invalid_value");
            result["port"] = number.ToString(CultureInfo.InvariantCulture);
        }
        else if (enabled) throw new InvalidNotificationException("port", "required");
        var security = Clean(values, "security") ?? "starttls";
        if (!Enum.TryParse<SmtpSecurity>(security, ignoreCase: true, out var parsedSecurity) || !Enum.IsDefined(parsedSecurity) || security.All(char.IsDigit))
            throw new InvalidNotificationException("security", "invalid_value");
        result["security"] = parsedSecurity.ToString().ToLowerInvariant();
        var username = Clean(values, "username");
        if (username is not null)
        {
            if (username.Length > 256) throw new InvalidNotificationException("username", "invalid_value");
            // Credentials never travel unencrypted.
            if (parsedSecurity == SmtpSecurity.None) throw new InvalidNotificationException("security", "insecure_authentication");
            result["username"] = username;
        }
        var from = Clean(values, "fromAddress");
        if (from is not null)
        {
            if (NotificationTargets.Normalize(NotificationChannelKind.Email, from) is null) throw new InvalidNotificationException("fromAddress", "invalid_value");
            result["fromAddress"] = from;
        }
        else if (enabled) throw new InvalidNotificationException("fromAddress", "required");
        var fromName = Clean(values, "fromName");
        if (fromName is not null)
        {
            if (fromName.Length > 100) throw new InvalidNotificationException("fromName", "invalid_value");
            result["fromName"] = fromName;
        }
        return result;
    }

    public static Dictionary<string, string> ValidateSms(IReadOnlyDictionary<string, string?> values, bool enabled)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var provider = Clean(values, "provider") ?? GenericHttpProvider;
        if (provider != GenericHttpProvider) throw new InvalidNotificationException("provider", "unsupported_provider");
        result["provider"] = provider;
        var endpoint = Clean(values, "endpoint");
        if (endpoint is not null)
        {
            result["endpoint"] = NotificationTargets.Normalize(NotificationChannelKind.Webhook, endpoint) ?? throw new InvalidNotificationException("endpoint", "invalid_value");
        }
        else if (enabled) throw new InvalidNotificationException("endpoint", "required");
        var authentication = Clean(values, "authentication") ?? "none";
        if (!Enum.TryParse<SmsAuthentication>(authentication, ignoreCase: true, out var parsed) || authentication.All(char.IsDigit)) throw new InvalidNotificationException("authentication", "invalid_value");
        result["authentication"] = parsed.ToString().ToLowerInvariant();
        var username = Clean(values, "username");
        if (parsed == SmsAuthentication.Basic)
        {
            result["username"] = username is { Length: <= 256 } ? username : throw new InvalidNotificationException("username", "required");
        }
        var header = Clean(values, "headerName");
        if (parsed == SmsAuthentication.Header)
        {
            if (header is null || !HeaderToken().IsMatch(header) || ForbiddenHeaders.Contains(header)) throw new InvalidNotificationException("headerName", "invalid_value");
            result["headerName"] = header;
        }
        var sender = Clean(values, "sender");
        if (sender is not null)
        {
            if (!SenderText().IsMatch(sender)) throw new InvalidNotificationException("sender", "invalid_value");
            result["sender"] = sender;
        }
        return result;
    }

    private static string? Clean(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? (value.Any(char.IsControl) ? throw new InvalidNotificationException(key, "invalid_value") : value.Trim()) : null;
}
