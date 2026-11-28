using System.Text.RegularExpressions;
using BackupPlatform.Api.Notifications;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Identity;
using BackupPlatform.Notifications;
using static BackupPlatform.UnitTests.Notifications.Support.NotificationTestData;

namespace BackupPlatform.UnitTests.Notifications;

public sealed partial class NotificationLanguageTests
{
    private static readonly string[] AllTypes =
    [
        NotificationEventTypes.BackupFailed, NotificationEventTypes.BackupStale, NotificationEventTypes.AgentOffline, NotificationEventTypes.QuotaCritical,
        NotificationEventTypes.ReplicaDegraded, NotificationEventTypes.StorageCapacityCritical, NotificationEventTypes.BackupSizeAnomaly,
        NotificationEventTypes.BackupShareCreated, NotificationEventTypes.BackupShareExtended, NotificationEventTypes.BackupShareRevoked, NotificationEventTypes.Test
    ];

    private static readonly string[] SecurityTypes =
    [
        NotificationEventTypes.SecurityPasswordChanged, NotificationEventTypes.SecurityTemporaryPassword, NotificationEventTypes.SecurityPasswordReset,
        NotificationEventTypes.SecurityAccountDisabled, NotificationEventTypes.SecurityAccountEnabled, NotificationEventTypes.SecurityFactorAdded,
        NotificationEventTypes.SecurityFactorRemoved, NotificationEventTypes.SecurityRecoveryRegenerated, NotificationEventTypes.SecurityRecoveryUsed,
        NotificationEventTypes.SecurityEmailChanged, NotificationEventTypes.SecuritySuspiciousLogin
    ];

    private static readonly Dictionary<string, string> Facts = new()
    {
        ["code"] = "disk_full", ["hours"] = "30", ["minutes"] = "42", ["percent"] = "93", ["state"] = "exceeded", ["status"] = "insufficient",
        ["verified"] = "1", ["required"] = "2", ["sizeBytes"] = "10", ["baselineBytes"] = "100", ["deviationPercent"] = "90"
    };

    private static readonly string[] Internal = ["disk_full", "exceeded", "insufficient", "backup_failed", "agent_offline", "BackupFailed", "Critical", "Warning"];

    [GeneratedRegex(@"\b(failed|offline|online|severity|automated|message|agent|backup|backups|share|shared|storage|quota|replicas?|job|again|unknown|manage|notifications?|console|time|open|address|ignore|confirm|resolved|created|revoked|extended|capacity|threshold|healthy|degraded|critical)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishWords();

    [GeneratedRegex("[ğüşıöçĞÜŞİÖÇ]")]
    private static partial Regex TurkishLetters();

    private static IEnumerable<DeliveryWork> Events()
    {
        foreach (var type in AllTypes)
            foreach (var phase in new[] { NotificationPhase.Raised, NotificationPhase.Recovered, NotificationPhase.Test })
                yield return Work(NotificationChannelKind.Email, "a@example.com", type: type, phase: phase, subjectName: "nightly", facts: Facts);
        yield return Work(NotificationChannelKind.Email, "a@example.com", type: NotificationEventTypes.RecipientVerification, subjectName: "nightly", facts: Facts);
        yield return Work(NotificationChannelKind.Email, "a@example.com", type: "unknown_future_type", subjectName: "nightly", facts: new Dictionary<string, string>());
    }

    private static string Visible(string text) => text.Replace(NotificationMessageFormatter.Product, "").Replace("nightly", "").Replace("(UTC)", "");

    [Fact]
    public void Every_event_renders_fully_in_turkish_in_turkish_mode()
    {
        foreach (var work in Events())
        {
            var message = NotificationMessageFormatter.Render(work.Event, NotificationLanguage.Turkish);
            foreach (var part in new[] { message.Subject, message.Text, message.Short })
            {
                var shown = Visible(part);
                Assert.True(!EnglishWords().IsMatch(shown), $"English word '{EnglishWords().Match(shown).Value}' in Turkish text ({work.Event.Type}/{work.Event.Phase}): {part}");
                foreach (var key in Internal) Assert.DoesNotContain(key, part);
            }
        }
    }

    [Fact]
    public void Every_event_renders_fully_in_english_in_english_mode()
    {
        foreach (var work in Events())
        {
            var message = NotificationMessageFormatter.Render(work.Event, NotificationLanguage.English);
            foreach (var part in new[] { message.Subject, message.Text, message.Short })
            {
                Assert.DoesNotMatch(TurkishLetters(), part);
                foreach (var key in Internal.Where(key => key.Contains('_') || key == "BackupFailed")) Assert.DoesNotContain(key, part);
            }
            if (work.Event.Type != NotificationEventTypes.RecipientVerification) Assert.Contains("Time (UTC)", message.Text);
        }
    }

    [Fact]
    public void Turkish_is_the_default_and_an_agent_is_called_a_backup_computer()
    {
        var work = Work(NotificationChannelKind.Email, "a@example.com", type: NotificationEventTypes.AgentOffline, subjectName: "build-01");
        var message = NotificationMessageFormatter.Render(work.Event);
        Assert.Contains("Yedekleme bilgisayarı çevrimdışı: build-01", message.Subject);
        Assert.Contains("Önem derecesi: Kritik", message.Text);
        Assert.Contains("Backup computer offline: build-01", NotificationMessageFormatter.Render(work.Event, NotificationLanguage.English).Subject);
    }

    [Theory]
    [InlineData(null, NotificationLanguage.Turkish)]
    [InlineData("", NotificationLanguage.Turkish)]
    [InlineData("tr", NotificationLanguage.Turkish)]
    [InlineData("en", NotificationLanguage.English)]
    [InlineData("en-US", NotificationLanguage.English)]
    [InlineData("en-US,en;q=0.9,tr;q=0.8", NotificationLanguage.English)]
    [InlineData("tr-TR,tr;q=0.9,en;q=0.8", NotificationLanguage.Turkish)]
    [InlineData("fr-FR,en;q=0.5", NotificationLanguage.English)]
    [InlineData("tr;q=0.4, en;q=0.8", NotificationLanguage.English)]
    [InlineData("de, fr", NotificationLanguage.Turkish)]
    public void The_language_comes_from_the_language_tag_or_header_and_defaults_to_turkish(string? header, NotificationLanguage expected) =>
        Assert.Equal(expected, NotificationLanguages.Parse(header));

    [Theory]
    [InlineData(AccountMailKind.PasswordReset)]
    [InlineData(AccountMailKind.Verification)]
    public void Account_mail_is_fully_turkish_or_fully_english(AccountMailKind kind)
    {
        var tr = AccountMailer.RenderAccountMail(kind, NotificationLanguage.Turkish);
        var en = AccountMailer.RenderAccountMail(kind, NotificationLanguage.English);
        Assert.Matches(TurkishLetters(), tr.Subject + tr.Text);
        Assert.DoesNotMatch(EnglishWords(), tr.Subject + tr.Text);
        Assert.DoesNotMatch(TurkishLetters(), en.Subject + en.Text);
        Assert.NotEqual(tr.Text, en.Text);
    }

    [Fact]
    public void Security_notices_are_fully_turkish_or_fully_english_for_every_type()
    {
        foreach (var type in SecurityTypes)
        {
            var tr = AccountMailer.RenderSecurityNotice(type, Start, NotificationLanguage.Turkish);
            var en = AccountMailer.RenderSecurityNotice(type, Start, NotificationLanguage.English);
            var trText = Visible(tr.Subject + tr.Body);
            Assert.DoesNotMatch(EnglishWords(), trText);
            Assert.Contains("Zaman (UTC)", tr.Body);
            Assert.DoesNotMatch(TurkishLetters(), en.Subject + en.Body);
            Assert.Contains("Time (UTC)", en.Body);
        }
    }

    [Fact]
    public void Email_footer_lines_follow_the_language()
    {
        Assert.Equal("Bu e-postaları almayı bırakmak için şu bağlantıyı açın:", NotificationMessageFormatter.UnsubscribeLine(NotificationLanguage.Turkish));
        Assert.Equal("To stop receiving these emails, open:", NotificationMessageFormatter.UnsubscribeLine(NotificationLanguage.English));
        Assert.DoesNotMatch(EnglishWords(), NotificationMessageFormatter.ConfirmLine(NotificationLanguage.Turkish));
    }
}
