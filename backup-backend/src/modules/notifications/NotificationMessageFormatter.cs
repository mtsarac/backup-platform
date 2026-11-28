using System.Globalization;
using BackupPlatform.Domain.Notifications;

namespace BackupPlatform.Notifications;

/// <summary>The text of one notification. <see cref="Short"/> fits one SMS-sized line.</summary>
public sealed record RenderedMessage(string Subject, string Text, string Short);

/// <summary>The language of a rendered message. Turkish is the product default, matching the web console.</summary>
public enum NotificationLanguage { Turkish, English }

public static class NotificationLanguages
{
    public const NotificationLanguage Default = NotificationLanguage.Turkish;

    /// <summary>
    /// Reads a language tag or an Accept-Language header value ("tr", "en-US", "en;q=0.9, tr;q=0.8"). The first entry with the highest quality
    /// that is Turkish or English wins; anything else, or nothing, is the default.
    /// </summary>
    public static NotificationLanguage Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return Default;
        NotificationLanguage? best = null;
        var bestQuality = -1d;
        foreach (var entry in header.Split(','))
        {
            var parts = entry.Split(';', 2, StringSplitOptions.TrimEntries);
            var tag = parts[0];
            var quality = 1d;
            if (parts.Length == 2 && parts[1].StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                && !double.TryParse(parts[1][2..], NumberStyles.Float, CultureInfo.InvariantCulture, out quality)) quality = 0d;
            NotificationLanguage? language =
                tag.Equals("tr", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("tr-", StringComparison.OrdinalIgnoreCase) ? NotificationLanguage.Turkish
                : tag.Equals("en", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ? NotificationLanguage.English
                : null;
            if (language is null || quality <= 0 || quality <= bestQuality) continue;
            best = language;
            bestQuality = quality;
        }
        return best ?? Default;
    }
}

/// <summary>
/// Turns an event into text. Only the event's own message-safe values are used (type, severity, subject display name, a few scalar facts): never a
/// path, a connection detail, a database or server name, or provider output. Free text (the subject name) is flattened to a single line first, so
/// it cannot break a header or forge a second paragraph. Internal keys (reason codes, states, severities) are translated, never shown raw.
/// </summary>
public static class NotificationMessageFormatter
{
    public const string Product = "Backup Platform";
    private const int ShortLimit = 160;

    public static RenderedMessage Render(NotificationEventSnapshot e, NotificationLanguage language = NotificationLanguages.Default)
    {
        var tr = language == NotificationLanguage.Turkish;
        // A confirmation request goes to an address nobody has agreed for yet, so it is fixed text: no tenant-chosen name can turn it into someone else's message.
        if (e.Type == NotificationEventTypes.RecipientVerification)
        {
            var subject = tr ? $"[{Product}] E-posta adresinizi doğrulayın" : $"[{Product}] Confirm your email address";
            return new RenderedMessage(subject, tr
                ? $"Bir {Product} hesabının yöneticisi, yedekleme uyarı e-postalarının bu adrese gönderilmesini istedi. Doğrulamadan önce hiçbir şey gönderilmez.\n\nBunu beklemiyorsanız bu e-postayı yok sayın."
                : $"An administrator of a {Product} account asked to send backup alert emails to this address. Nothing is sent until you confirm.\n\nIf you did not expect this, ignore this email.", subject);
        }
        var name = Flatten(e.SubjectName);
        var (title, detail) = e.Phase switch
        {
            NotificationPhase.Test => tr
                ? ("Test bildirimi", $"Bu, \"{name}\" kanalı için bir test bildirimidir. Bunu okuyabiliyorsanız gönderim çalışıyor demektir.")
                : ("Test notification", $"This is a test notification for the channel \"{name}\". If you can read it, delivery works."),
            NotificationPhase.Recovered => Recovered(e.Type, name, tr),
            _ => Raised(e, name, tr)
        };
        var severityWord = SeverityText(e.Severity, tr);
        var label = e.Phase == NotificationPhase.Raised ? severityWord
            : e.Phase == NotificationPhase.Recovered ? (tr ? "Çözüldü" : "Recovered") : (tr ? "Test" : "Test");
        var text = tr
            ? $"{detail}\n\nÖnem derecesi: {severityWord}\nZaman (UTC): {e.OccurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n\nBu ileti {Product} tarafından otomatik olarak gönderilmiştir. Bildirimleri web konsolunda Bildirimler bölümünden yönetebilirsiniz."
            : $"{detail}\n\nSeverity: {severityWord}\nTime (UTC): {e.OccurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n\nThis is an automated message from {Product}. Manage notifications under Notifications in the web console.";
        var shortText = $"[{Product}] {label.ToUpper(Culture(tr))}: {title}";
        return new RenderedMessage($"[{Product}] {title}", text, shortText.Length <= ShortLimit ? shortText : shortText[..(ShortLimit - 1)] + "…");
    }

    /// <summary>The footer lines of an email: how to confirm a recipient address, and how to stop receiving mail.</summary>
    public static string ConfirmLine(NotificationLanguage language) => language == NotificationLanguage.Turkish ? "Bu adresi doğrulamak için şu bağlantıyı açın:" : "To confirm this address, open:";

    public static string UnsubscribeLine(NotificationLanguage language) => language == NotificationLanguage.Turkish ? "Bu e-postaları almayı bırakmak için şu bağlantıyı açın:" : "To stop receiving these emails, open:";

    private static CultureInfo Culture(bool tr) => tr ? CultureInfo.GetCultureInfo("tr-TR") : CultureInfo.InvariantCulture;

    private static string SeverityText(NotificationSeverity severity, bool tr) => severity switch
    {
        NotificationSeverity.Critical => tr ? "Kritik" : "Critical",
        NotificationSeverity.Warning => tr ? "Uyarı" : "Warning",
        _ => tr ? "Bilgi" : "Info"
    };

    private static (string Title, string Detail) Raised(NotificationEventSnapshot e, string name, bool tr)
    {
        string Fact(string key) => e.Facts.TryGetValue(key, out var value) ? Flatten(value) : (tr ? "bilinmiyor" : "unknown");
        string Fixed(string key, string fallback) => e.Facts.TryGetValue(key, out var value) ? Flatten(value) : fallback;
        var quota = Fixed("state", "critical") == "exceeded" ? (tr ? "aşıldı" : "exceeded") : (tr ? "kritik" : "critical");
        var replicas = Fixed("status", "degraded") == "insufficient" ? (tr ? "yetersiz" : "insufficient") : (tr ? "azaldı" : "degraded");
        var reason = ReasonText(e.Facts.TryGetValue("code", out var code) ? code : null, tr);
        return e.Type switch
        {
            NotificationEventTypes.BackupFailed => tr
                ? ($"Yedekleme başarısız: {name}", $"\"{name}\" işinin bir yedekleme denemesi başarısız oldu ({reason}).")
                : ($"Backup failed: {name}", $"A backup run of job \"{name}\" failed ({reason})."),
            NotificationEventTypes.BackupStale => tr
                ? ($"Yakın zamanda başarılı yedekleme yok: {name}", $"\"{name}\" işi yaklaşık {Fact("hours")} saattir başarılı bir yedekleme tamamlamadı.")
                : ($"No recent successful backup: {name}", $"Job \"{name}\" has not completed a successful backup for about {Fact("hours")} hours."),
            NotificationEventTypes.AgentOffline => tr
                ? ($"Yedekleme bilgisayarı çevrimdışı: {name}", $"\"{name}\" yedekleme bilgisayarı yaklaşık {Fact("minutes")} dakikadır görülmedi.")
                : ($"Backup computer offline: {name}", $"Backup computer \"{name}\" has not been seen for about {Fact("minutes")} minutes."),
            NotificationEventTypes.QuotaCritical => tr
                ? ($"Depolama kotası {quota}: %{Fact("percent")} kullanıldı", $"Yedekleme depolama alanı sınırın %{Fact("percent")} düzeyinde ({quota}).")
                : ($"Storage quota {quota}: {Fact("percent")}% used", $"The backup storage is at {Fact("percent")}% of its limit ({quota})."),
            NotificationEventTypes.ReplicaDegraded => tr
                ? ($"Yedek kopyaları {replicas}: {name}", $"\"{name}\" işinin son yedeği için gereken {Fact("required")} doğrulanmış kopyadan yalnızca {Fact("verified")} tanesi var.")
                : ($"Backup replicas {replicas}: {name}", $"The latest backup of job \"{name}\" has {Fact("verified")} of {Fact("required")} required verified replicas."),
            NotificationEventTypes.StorageCapacityCritical => tr
                ? ($"Depolama kapasitesi kritik: {name}", $"\"{name}\" depolama hedefi %{Fact("percent")} doluluğa ulaştı (%90 kritik eşik aşıldı).")
                : ($"Storage capacity critical: {name}", $"Storage target \"{name}\" is at {Fact("percent")}% capacity (critical threshold 90% exceeded)."),
            NotificationEventTypes.BackupSizeAnomaly => tr
                ? ($"Yedek boyutunda olağandışı değişim: {name}", $"\"{name}\" işinin yedeği başarıyla tamamlandı ancak boyutu olağandışı: {Fact("sizeBytes")} bayt (son ortalama {Fact("baselineBytes")} bayt, %{Fact("deviationPercent")} değişim). Bu yalnızca bir uyarıdır, yedeği geçersiz kılmaz.")
                : ($"Backup size anomaly: {name}", $"A backup of job \"{name}\" succeeded with an unusual backup file size: {Fact("sizeBytes")} bytes compared with a recent average of {Fact("baselineBytes")} bytes ({Fact("deviationPercent")}% change). This advisory does not invalidate the backup."),
            NotificationEventTypes.BackupShareCreated => tr
                ? ($"Paylaşım oluşturuldu: {name}", $"\"{name}\" için bir yedek paylaşımı oluşturuldu.")
                : ($"Backup share created: {name}", $"A backup share was created for \"{name}\"."),
            NotificationEventTypes.BackupShareExtended => tr
                ? ($"Paylaşım süresi uzatıldı: {name}", $"\"{name}\" için bir yedek paylaşımının süresi uzatıldı.")
                : ($"Backup share extended: {name}", $"A backup share for \"{name}\" was extended."),
            NotificationEventTypes.BackupShareRevoked => tr
                ? ($"Paylaşım iptal edildi: {name}", $"\"{name}\" için bir yedek paylaşımı iptal edildi.")
                : ($"Backup share revoked: {name}", $"A backup share for \"{name}\" was revoked."),
            _ => tr
                ? ($"Bildirim: {name}", $"\"{name}\" için bir olay gerçekleşti.")
                : ($"Notification: {name}", $"An event occurred for \"{name}\".")
        };
    }

    private static (string Title, string Detail) Recovered(string type, string name, bool tr) => type switch
    {
        NotificationEventTypes.BackupStale => tr
            ? ($"Yedeklemeler yeniden başarılı: {name}", $"\"{name}\" işi yeniden başarılı bir yedekleme tamamladı.")
            : ($"Backups succeeding again: {name}", $"Job \"{name}\" completed a successful backup again."),
        NotificationEventTypes.AgentOffline => tr
            ? ($"Yedekleme bilgisayarı yeniden çevrimiçi: {name}", $"\"{name}\" yedekleme bilgisayarı yeniden bağlandı.")
            : ($"Backup computer back online: {name}", $"Backup computer \"{name}\" is connected again."),
        NotificationEventTypes.QuotaCritical => tr
            ? ("Depolama kotası normale döndü", "Yedekleme depolama alanı yeniden kritik eşiğin altına indi.")
            : ("Storage quota back to normal", "The backup storage is below the critical threshold again."),
        NotificationEventTypes.ReplicaDegraded => tr
            ? ($"Yedek kopyaları yeniden sağlıklı: {name}", $"\"{name}\" işinin son yedeği kopya gereksinimini yeniden karşılıyor.")
            : ($"Backup replicas healthy again: {name}", $"The latest backup of job \"{name}\" meets its replica requirement again."),
        NotificationEventTypes.StorageCapacityCritical => tr
            ? ($"Depolama kapasitesi yeniden sağlıklı: {name}", $"\"{name}\" depolama hedefi yeniden kritik eşiğin altında.")
            : ($"Storage capacity healthy again: {name}", $"Storage target \"{name}\" is below the critical threshold again."),
        _ => tr
            ? ($"Çözüldü: {name}", $"\"{name}\" için oluşan durum sona erdi.")
            : ($"Resolved: {name}", $"The condition for \"{name}\" has ended.")
    };

    /// <summary>A reason code is an internal key: known codes read as a sentence fragment, unknown ones are not shown at all.</summary>
    private static string ReasonText(string? code, bool tr) => code switch
    {
        "agent_lost" => tr ? "yedekleme bilgisayarıyla bağlantı kesildi" : "the backup computer lost contact",
        "timeout" => tr ? "zaman aşımına uğradı" : "it timed out",
        "authentication_failed" => tr ? "kimlik doğrulaması başarısız" : "authentication failed",
        "network_denied" => tr ? "sunucuya erişim ağ güvenlik politikası tarafından engellendi" : "access was blocked by the network security policy",
        "network_unavailable" => tr ? "depolama sunucusuna ağ üzerinden ulaşılamadı" : "the storage server could not be reached over the network",
        "secret_unavailable" => tr ? "kimlik bilgisi kullanılamıyor" : "the credential is unavailable",
        "insufficient_space" or "disk_full" => tr ? "depolamada yeterli boş alan yok" : "there is not enough free space in storage",
        "invalid_configuration" => tr ? "bağlantı ayarı geçersiz" : "the connection setting is invalid",
        "admission_denied" => tr ? "yeni yedekleme başlatılamadı" : "a new backup could not be started",
        _ => tr ? "ayrıntılar için web konsoluna bakın" : "see the web console for details"
    };

    /// <summary>One line, no control characters, bounded; applied to every free-text value that reaches a message.</summary>
    public static string Flatten(string value)
    {
        var chars = value.Select(character => char.IsControl(character) ? ' ' : character).ToArray();
        var text = new string(chars).Trim();
        return text.Length <= 100 ? text : text[..100];
    }
}
