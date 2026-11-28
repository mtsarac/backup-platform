using System.Globalization;

namespace BackupPlatform.Agent.Host;

internal enum AgentMessage
{
    ServerInvalid,
    TokenMalformed,
    NameInvalid,
    AlreadyEnrolled,
    UnreadableEnrollmentResponse,
    InvalidIdentity,
    IdentityReloadFailed,
    IdentitySaveFailed,
    IdentityVerifyFailed,
    TlsFailed,
    NameResolutionFailed,
    ConnectionFailed,
    RequestFailed,
    TokenMayBeConsumed,
    RequestTimedOut,
    DirectoryIsLink,
    DirectoryNotOwnerOnly,
    LockIsLink,
    LockNotOwnerOnly,
    EnrollmentBusy,
    StoreAccessDenied,
    StoreNotAbsolute,
    PromptServer,
    PromptToken,
    TokenPromptNeedsTerminal,
    EnrollmentVerified,
    EnrollmentCancelled,
    StoreUnusable,
    DuplicateOption,
    EnrollUsage,
    CertificateUsage,
    CertificatePasswordRequired,
    CertificateEnrollFirst,
    CertificateRotationInvalid,
    CertificateSaved,
    CertificateFailed,
    CertificateRejected,
    WindowsNeedsElevation,
    WindowsAlreadyEnrolled,
    WindowsInstallerMissing,
    PowerShellStartFailed,
    PowerShellStartDenied,
    ServiceIdentityMissing,
    ServiceIdentityVerified,
    ServiceRequestUnreadable,
    StagingPermissionsUntrusted,
    ServiceInstallerRequired,
    UpdateFailed,
    TokenExpired,
    TokenConsumed,
    TokenInvalid,
    EnrollmentDisabled,
    ProtocolIncompatible,
    BackendRejected,
}

/// <summary>Language of the text the agent host writes to a console or hands to an installer; English unless Turkish is requested or the system UI language is Turkish.</summary>
internal static class AgentText
{
    internal const string LanguageVariable = "BACKUP_AGENT_LANGUAGE";

    private static volatile string current = Resolve(null, Environment.GetEnvironmentVariable(LanguageVariable), CultureInfo.CurrentUICulture);

    internal static string Language => current;

    internal static void Use(string language) => current = language is "tr" ? "tr" : "en";

    /// <summary>An explicit value wins, then the environment variable, then the UI culture; anything unrecognised falls back to English.</summary>
    internal static string Resolve(string? explicitLanguage, string? environmentLanguage, CultureInfo culture)
    {
        foreach (var candidate in new[] { explicitLanguage, environmentLanguage })
        {
            if (candidate is "tr" or "en") { return candidate; }
        }
        return string.Equals(culture.TwoLetterISOLanguageName, "tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
    }

    /// <summary>Applies an explicit --language option found anywhere in the arguments.</summary>
    internal static void ConfigureFrom(string[] args)
    {
        string? explicitLanguage = null;
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == "--language") { explicitLanguage = args[index + 1]; }
        }
        current = Resolve(explicitLanguage, Environment.GetEnvironmentVariable(LanguageVariable), CultureInfo.CurrentUICulture);
    }

    internal static string Get(AgentMessage message) => Get(message, current);

    internal static string Get(AgentMessage message, string language) => language == "tr" ? Messages[message].Turkish : Messages[message].English;

    internal static string Format(AgentMessage message, params object[] values) => string.Format(CultureInfo.InvariantCulture, Get(message), values);

    internal static IReadOnlyDictionary<AgentMessage, (string English, string Turkish)> All => Messages;

    private static readonly Dictionary<AgentMessage, (string English, string Turkish)> Messages = new()
    {
        [AgentMessage.ServerInvalid] = ("The server must be an absolute HTTPS address without user information, query or fragment.",
            "Sunucu, kullanıcı bilgisi, sorgu veya parça içermeyen mutlak bir HTTPS adresi olmalıdır."),
        [AgentMessage.TokenMalformed] = ("The enrollment token is malformed. Copy the complete token from the Add backup computer window.",
            "Kayıt anahtarı geçersiz. Anahtarın tamamını Yedekleme bilgisayarı ekle penceresinden kopyalayın."),
        [AgentMessage.NameInvalid] = ("The computer name must contain 1-255 characters and no control characters.",
            "Bilgisayar adı, denetim karakteri içermeyen 1-255 karakter olmalıdır."),
        [AgentMessage.AlreadyEnrolled] = ("This store is already enrolled. The existing identity was preserved and the token was not used.",
            "Bu depo zaten kayıtlı. Mevcut kimlik korundu ve kayıt anahtarı kullanılmadı."),
        [AgentMessage.UnreadableEnrollmentResponse] = ("The backend returned an unreadable enrollment response. The token may have been consumed; check the Add backup computer window before trying another token.",
            "Sunucu okunamayan bir kayıt yanıtı verdi. Kayıt anahtarı harcanmış olabilir; başka bir anahtar denemeden önce Yedekleme bilgisayarı ekle penceresini kontrol edin."),
        [AgentMessage.InvalidIdentity] = ("The backend returned an invalid identity. Nothing was saved; the token may have been consumed.",
            "Sunucu geçersiz bir kimlik verdi. Hiçbir şey kaydedilmedi; kayıt anahtarı harcanmış olabilir."),
        [AgentMessage.IdentityReloadFailed] = ("The saved identity could not be reloaded. The token has been consumed; repair the secure store before retrying.",
            "Kaydedilen kimlik yeniden okunamadı. Kayıt anahtarı harcandı; yeniden denemeden önce güvenli depoyu onarın."),
        [AgentMessage.IdentitySaveFailed] = ("The backend accepted enrollment, but the secure identity could not be saved or reloaded. The token was consumed. Check store ownership, permissions and disk space; any stored identity was preserved.",
            "Sunucu kaydı kabul etti, ancak güvenli kimlik kaydedilemedi veya yeniden okunamadı. Kayıt anahtarı harcandı. Deponun sahipliğini, izinlerini ve disk alanını kontrol edin; mevcut kimlik korundu."),
        [AgentMessage.IdentityVerifyFailed] = ("The saved identity could not be verified by the backend. It was preserved; check the backend and secure store, then restart the agent. Do not enroll again.",
            "Kaydedilen kimlik sunucu tarafından doğrulanamadı. Kimlik korundu; sunucuyu ve güvenli depoyu kontrol edip yedekleme programını yeniden başlatın. Yeniden kayıt yapmayın."),
        [AgentMessage.TlsFailed] = ("The backend TLS connection failed. Check that its HTTPS certificate is trusted, valid for this server name and not expired.",
            "Sunucunun TLS bağlantısı kurulamadı. HTTPS sertifikasının güvenilir, bu sunucu adı için geçerli ve süresi dolmamış olduğunu kontrol edin."),
        [AgentMessage.NameResolutionFailed] = ("The backend server name could not be resolved. Check the HTTPS address and DNS configuration.",
            "Sunucu adı çözümlenemedi. HTTPS adresini ve DNS yapılandırmasını kontrol edin."),
        [AgentMessage.ConnectionFailed] = ("The HTTPS backend could not be reached. Check the server address, network, firewall and backend availability.",
            "HTTPS sunucusuna ulaşılamadı. Sunucu adresini, ağı, güvenlik duvarını ve sunucunun çalıştığını kontrol edin."),
        [AgentMessage.RequestFailed] = ("The HTTPS backend request failed. Check backend availability and the network connection.",
            "HTTPS sunucu isteği başarısız oldu. Sunucunun çalıştığını ve ağ bağlantısını kontrol edin."),
        [AgentMessage.TokenMayBeConsumed] = ("A submitted token may have been consumed; check the Add backup computer window before retrying.",
            "Gönderilen kayıt anahtarı harcanmış olabilir; yeniden denemeden önce Yedekleme bilgisayarı ekle penceresini kontrol edin."),
        [AgentMessage.RequestTimedOut] = ("The backend request timed out. A submitted token may have been consumed; check the Add backup computer window before retrying.",
            "Sunucu isteği zaman aşımına uğradı. Gönderilen kayıt anahtarı harcanmış olabilir; yeniden denemeden önce Yedekleme bilgisayarı ekle penceresini kontrol edin."),
        [AgentMessage.DirectoryIsLink] = ("The enrollment directory must not be a symbolic link.",
            "Kayıt dizini sembolik bağ olmamalıdır."),
        [AgentMessage.DirectoryNotOwnerOnly] = ("The enrollment directory must be owner-only (mode 0700).",
            "Kayıt dizinine yalnızca sahibi erişebilmelidir (kip 0700)."),
        [AgentMessage.LockIsLink] = ("The enrollment lock must not be a link.",
            "Kayıt kilidi bağ olmamalıdır."),
        [AgentMessage.LockNotOwnerOnly] = ("The enrollment lock must be an owner-only regular file (mode 0600).",
            "Kayıt kilidi yalnızca sahibinin erişebildiği sıradan bir dosya olmalıdır (kip 0600)."),
        [AgentMessage.EnrollmentBusy] = ("Another enrollment is in progress, or the secure store cannot be locked. No token was submitted.",
            "Başka bir kayıt sürüyor veya güvenli depo kilitlenemiyor. Hiçbir kayıt anahtarı gönderilmedi."),
        [AgentMessage.StoreAccessDenied] = ("Access to the enrollment store was denied. Run as the agent's service account or choose an owner-only store directory.",
            "Kayıt deposuna erişim reddedildi. Yedekleme programının hizmet hesabıyla çalıştırın veya yalnızca sahibinin erişebildiği bir depo dizini seçin."),
        [AgentMessage.StoreNotAbsolute] = ("The credential store directory must be an absolute path.",
            "Kimlik deposu dizini mutlak bir yol olmalıdır."),
        [AgentMessage.PromptServer] = ("HTTPS backend address: ", "HTTPS sunucu adresi: "),
        [AgentMessage.PromptToken] = ("Enrollment token (hidden): ", "Kayıt anahtarı (gizli): "),
        [AgentMessage.TokenPromptNeedsTerminal] = ("Token prompting requires a terminal. Supply --token with --non-interactive instead.",
            "Kayıt anahtarını sormak için terminal gerekir. Bunun yerine --non-interactive ile birlikte --token verin."),
        [AgentMessage.EnrollmentVerified] = ("Enrollment verified for agent {0:D}. Identity saved securely. Start this host with the same service account and store directory to connect.",
            "{0:D} kimlikli yedekleme bilgisayarının kaydı doğrulandı. Kimlik güvenli şekilde kaydedildi. Bağlanmak için bu programı aynı hizmet hesabı ve depo dizini ile başlatın."),
        [AgentMessage.EnrollmentCancelled] = ("Enrollment was cancelled. If submitted, the token may have been consumed; check the Add backup computer window. Any saved identity was preserved.",
            "Kayıt iptal edildi. Gönderildiyse kayıt anahtarı harcanmış olabilir; Yedekleme bilgisayarı ekle penceresini kontrol edin. Kaydedilmiş kimlik korundu."),
        [AgentMessage.StoreUnusable] = ("The secure enrollment store could not be used. Check the absolute store path, ownership and permissions. No existing identity was replaced.",
            "Güvenli kayıt deposu kullanılamadı. Deponun mutlak yolunu, sahipliğini ve izinlerini kontrol edin. Mevcut kimlik değiştirilmedi."),
        [AgentMessage.DuplicateOption] = ("Duplicate enrollment option.", "Kayıt seçeneği birden fazla verildi."),
        [AgentMessage.EnrollUsage] = ("Usage: enroll --server https://backend/ [--token TOKEN] [--name NAME] [--store-directory ABSOLUTE_PATH] [--language tr|en] [--non-interactive] [--console].",
            "Kullanım: enroll --server https://sunucu/ [--token ANAHTAR] [--name AD] [--store-directory MUTLAK_YOL] [--language tr|en] [--non-interactive] [--console]."),
        [AgentMessage.CertificateUsage] = ("Usage: certificate enroll|rotate [credential-store-directory]. Stop the service first.",
            "Kullanım: certificate enroll|rotate [kimlik-deposu-dizini]. Önce hizmeti durdurun."),
        [AgentMessage.CertificatePasswordRequired] = ("A PFX password of at least 12 characters is required in AGENT_MTLS_CERTIFICATE_PASSWORD.",
            "AGENT_MTLS_CERTIFICATE_PASSWORD içinde en az 12 karakterli bir PFX parolası gerekir."),
        [AgentMessage.CertificateEnrollFirst] = ("Enroll the agent before requesting a certificate.",
            "Sertifika istemeden önce yedekleme bilgisayarını kaydedin."),
        [AgentMessage.CertificateRotationInvalid] = ("The backend returned an invalid rotation response.",
            "Sunucu geçersiz bir yenileme yanıtı verdi."),
        [AgentMessage.CertificateSaved] = ("Client certificate saved. Configure AGENT_MTLS_CERTIFICATE_PATH to the credential store's client-certificate.pfx and restart the service.",
            "İstemci sertifikası kaydedildi. AGENT_MTLS_CERTIFICATE_PATH değerini kimlik deposundaki client-certificate.pfx dosyasına ayarlayıp hizmeti yeniden başlatın."),
        [AgentMessage.CertificateFailed] = ("Certificate enrollment did not complete. Check issuer configuration, TLS trust, store permissions and backend availability; rerun the command before restarting the service.",
            "Sertifika kaydı tamamlanamadı. Sertifika düzenleyici yapılandırmasını, TLS güvenini, depo izinlerini ve sunucunun çalıştığını kontrol edin; hizmeti yeniden başlatmadan önce komutu tekrar çalıştırın."),
        [AgentMessage.CertificateRejected] = ("The backend rejected certificate enrollment.",
            "Sunucu sertifika kaydını reddetti."),
        [AgentMessage.WindowsNeedsElevation] = ("Windows enrollment requires an elevated terminal to install NT SERVICE\\BackupAgent. Run this same enroll command as administrator, or use deploy\\windows\\Install-BackupAgentService.ps1 from elevated PowerShell. No identity was changed.",
            "Windows'ta kayıt için NT SERVICE\\BackupAgent hizmetini kurmak üzere yükseltilmiş bir terminal gerekir. Aynı enroll komutunu yönetici olarak çalıştırın veya yükseltilmiş PowerShell'den deploy\\windows\\Install-BackupAgentService.ps1 betiğini kullanın. Hiçbir kimlik değiştirilmedi."),
        [AgentMessage.WindowsAlreadyEnrolled] = ("This store is already enrolled. The existing identity was preserved and the token was not used. Reinstall through Install-BackupAgentService.ps1 without EnrollmentToken.",
            "Bu depo zaten kayıtlı. Mevcut kimlik korundu ve kayıt anahtarı kullanılmadı. EnrollmentToken vermeden Install-BackupAgentService.ps1 ile yeniden kurun."),
        [AgentMessage.WindowsInstallerMissing] = ("The published Windows installer is missing. Use a complete published agent folder containing deploy\\windows\\Install-BackupAgentService.ps1.",
            "Yayımlanmış Windows kurulum betiği bulunamadı. deploy\\windows\\Install-BackupAgentService.ps1 dosyasını içeren eksiksiz bir yayımlanmış klasör kullanın."),
        [AgentMessage.PowerShellStartFailed] = ("Windows PowerShell could not start the service installer.",
            "Windows PowerShell hizmet kurulum betiğini başlatamadı."),
        [AgentMessage.PowerShellStartDenied] = ("Windows PowerShell could not start the service installer. Check the published folder and administrator permissions.",
            "Windows PowerShell hizmet kurulum betiğini başlatamadı. Yayımlanmış klasörü ve yönetici izinlerini kontrol edin."),
        [AgentMessage.ServiceIdentityMissing] = ("No saved identity exists. Supply an enrollment token or run the installer interactively.",
            "Kaydedilmiş kimlik yok. Bir kayıt anahtarı verin veya kurulumu etkileşimli çalıştırın."),
        [AgentMessage.ServiceIdentityVerified] = ("The service identity was loaded and verified successfully.",
            "Hizmet kimliği yüklendi ve başarıyla doğrulandı."),
        [AgentMessage.ServiceRequestUnreadable] = ("The protected enrollment request could not be read, or enrollment was cancelled. Any existing identity was preserved.",
            "Korumalı kayıt isteği okunamadı veya kayıt iptal edildi. Mevcut kimlik korundu."),
        [AgentMessage.StagingPermissionsUntrusted] = ("The enrollment staging permissions admit an untrusted account.",
            "Kayıt hazırlık klasörünün izinleri güvenilmeyen bir hesaba erişim veriyor."),
        [AgentMessage.ServiceInstallerRequired] = ("Use the Windows Service installer to provision this agent.",
            "Bu yedekleme bilgisayarını hazırlamak için Windows hizmet kurulum betiğini kullanın."),
        [AgentMessage.UpdateFailed] = ("The agent update failed. The previous identity and configuration were preserved.",
            "Yedekleme programı güncellemesi başarısız oldu. Önceki kimlik ve yapılandırma korundu."),
        [AgentMessage.TokenExpired] = ("The enrollment token expired. Generate a new token in the Add backup computer window.",
            "Kayıt anahtarının süresi doldu. Yedekleme bilgisayarı ekle penceresinde yeni bir anahtar oluşturun."),
        [AgentMessage.TokenConsumed] = ("The enrollment token was already used. Check the computer in the Add backup computer window or generate a new token for another machine.",
            "Kayıt anahtarı daha önce kullanıldı. Yedekleme bilgisayarı ekle penceresinden bilgisayarı kontrol edin veya başka bir makine için yeni bir anahtar oluşturun."),
        [AgentMessage.TokenInvalid] = ("The enrollment token is invalid or revoked. Generate a new token in the Add backup computer window.",
            "Kayıt anahtarı geçersiz veya iptal edilmiş. Yedekleme bilgisayarı ekle penceresinde yeni bir anahtar oluşturun."),
        [AgentMessage.EnrollmentDisabled] = ("Enrollment is disabled for this tenant. Ask your administrator to enable it.",
            "Bu kiracı için kayıt kapalı. Yöneticinizden açmasını isteyin."),
        [AgentMessage.ProtocolIncompatible] = ("The backend and agent protocol versions are incompatible. Install a compatible agent release.",
            "Sunucu ile yedekleme programının protokol sürümleri uyumsuz. Uyumlu bir sürüm kurun."),
        [AgentMessage.BackendRejected] = ("The backend rejected enrollment (HTTP {0}). Check tenant enrollment permissions and use a fresh token.",
            "Sunucu kaydı reddetti (HTTP {0}). Kiracının kayıt izinlerini kontrol edin ve yeni bir kayıt anahtarı kullanın."),
    };
}
