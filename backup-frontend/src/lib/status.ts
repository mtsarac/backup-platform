import { tx } from '@/lib/i18n'

/** safe / attention / urgent are the three health levels; the rest are neutral states that carry no verdict. */
export type StatusLevel = 'safe' | 'attention' | 'urgent' | 'running' | 'pending' | 'info' | 'neutral'
export interface StatusMeta { level: StatusLevel; label: string }

/** The word a level says by itself; neutral has none, so a neutral pill always brings its own label. */
export function levelLabel(level: StatusLevel): string | undefined {
  switch (level) {
    case 'safe': return tx("Güvende")
    case 'attention': return tx("Dikkat")
    case 'urgent': return tx("Acil")
    case 'running': return tx("Çalışıyor")
    case 'pending': return tx("Beklemede")
    case 'info': return tx("Bilgi")
    default: return undefined
  }
}

/**
 * The one status dictionary. The same word can mean different things in different places (an expired subscription is urgent,
 * an expired emergency request is just over), so entries are grouped by domain. Labels are getters so they follow the language.
 */
const dictionary = {
  general: {
    Pending: { level: 'pending', get label() { return tx("Sırada bekliyor") } },
    Leased: { level: 'running', get label() { return tx("Agent görevi aldı") } },
    Running: { level: 'running', get label() { return tx("Çalışıyor") } },
    RetryPending: { level: 'attention', get label() { return tx("Yeniden denenecek") } },
    Paused: { level: 'attention', get label() { return tx('Duraklatıldı') } },
    CancellationRequested: { level: 'attention', get label() { return tx("İptal isteniyor") } },
    Succeeded: { level: 'safe', get label() { return tx("Başarılı") } },
    Failed: { level: 'urgent', get label() { return tx("Başarısız") } },
    Cancelled: { level: 'neutral', get label() { return tx("İptal edildi") } },
    AgentLost: { level: 'urgent', get label() { return tx("Agent bağlantısı kaybedildi") } },
    Active: { level: 'safe', get label() { return tx("Etkin") } },
    Disabled: { level: 'attention', get label() { return tx("Devre dışı") } },
    Revoked: { level: 'urgent', get label() { return tx("Erişimi iptal edildi") } },
    online: { level: 'safe', get label() { return tx("Çevrimiçi") } },
    offline: { level: 'neutral', get label() { return tx("Çevrimdışı") } },
    unknown: { level: 'neutral', get label() { return tx("Bağlantı bilinmiyor") } },
    Available: { level: 'safe', get label() { return tx("Kullanılabilir") } },
    Quarantined: { level: 'attention', get label() { return tx("Karantinada · sahiplik incelemesi gerekli") } },
    DeletePending: { level: 'attention', get label() { return tx("Silme bekliyor") } },
    DeleteFailed: { level: 'urgent', get label() { return tx("Silme başarısız") } },
    Deleted: { level: 'neutral', get label() { return tx("Silindi") } },
    Deleting: { level: 'attention', get label() { return tx("Fiziksel silme sürüyor") } },
    Verified: { level: 'safe', get label() { return tx("Doğrulandı") } },
  },
  agent: {
    Active: { level: 'safe', get label() { return tx("Etkin") } },
    PendingEnrollment: { level: 'pending', get label() { return tx("Kayıt bekliyor") } },
    Disabled: { level: 'attention', get label() { return tx("Devre dışı") } },
    Revoked: { level: 'urgent', get label() { return tx("İptal edildi") } },
    VersionMismatch: { level: 'urgent', get label() { return tx("Protokol sürümü uyumsuz") } },
    Online: { level: 'safe', get label() { return tx("Çevrimiçi") } },
    Offline: { level: 'neutral', get label() { return tx("Çevrimdışı") } },
    Unknown: { level: 'neutral', get label() { return tx("Bilinmiyor") } },
    online: { level: 'safe', get label() { return tx("Çevrimiçi") } },
    offline: { level: 'neutral', get label() { return tx("Çevrimdışı") } },
    unknown: { level: 'neutral', get label() { return tx("Bilinmiyor") } },
  },
  capability: {
    Supported: { level: 'safe', get label() { return tx("Destekleniyor") } },
    Unsupported: { level: 'neutral', get label() { return tx("Desteklenmiyor") } },
    Unknown: { level: 'attention', get label() { return tx("Bilinmiyor") } },
  },
  replication: {
    Healthy: { level: 'safe', get label() { return tx("Replikasyon sağlıklı") } },
    Degraded: { level: 'attention', get label() { return tx("Kısmi replikasyon · politika karşılandı") } },
    InsufficientReplicas: { level: 'urgent', get label() { return tx("Yetersiz doğrulanmış replika") } },
    Unavailable: { level: 'urgent', get label() { return tx("Doğrulanmış replika yok") } },
  },
  destination: {
    Verified: { level: 'safe', get label() { return tx("Doğrulandı") } },
    Failed: { level: 'urgent', get label() { return tx("Başarısız") } },
    Cancelled: { level: 'neutral', get label() { return tx("İptal edildi") } },
    NotAttempted: { level: 'neutral', get label() { return tx("Denenmedi") } },
    Deleted: { level: 'neutral', get label() { return tx("Silindi") } },
  },
  reachability: {
    Reachable: { level: 'safe', get label() { return tx("Erişilebilir") } },
    Unreachable: { level: 'urgent', get label() { return tx("Erişilemiyor") } },
    Unknown: { level: 'neutral', get label() { return tx("Bilinmiyor") } },
  },
  access: {
    CredentialsRequired: { level: 'attention', get label() { return tx("Kimlik bilgisi gerekli") } },
    Authenticated: { level: 'safe', get label() { return tx("Kimlik doğrulandı") } },
    AuthenticationFailed: { level: 'urgent', get label() { return tx("Kimlik doğrulaması başarısız") } },
    Unreachable: { level: 'urgent', get label() { return tx("Bağlanılamadı") } },
    Error: { level: 'urgent', get label() { return tx("Hata") } },
    Unknown: { level: 'neutral', get label() { return tx("Bilinmiyor") } },
  },
  presence: {
    Present: { level: 'safe', get label() { return tx("Mevcut") } },
    Missing: { level: 'neutral', get label() { return tx("Kayıp") } },
  },
  lastChange: {
    Discovered: { level: 'info', get label() { return tx("Yeni") } },
    Changed: { level: 'attention', get label() { return tx("Değişti") } },
    Recovered: { level: 'safe', get label() { return tx("Yeniden erişilebilir") } },
    Unreachable: { level: 'urgent', get label() { return tx("Erişilemiyor") } },
    Missing: { level: 'neutral', get label() { return tx("Kayboldu") } },
    Reappeared: { level: 'info', get label() { return tx("Yeniden göründü") } },
  },
  fullBackup: {
    Supported: { level: 'safe', get label() { return tx("Full Backup destekleniyor") } },
    Unsupported: { level: 'urgent', get label() { return tx("Full Backup desteklenmiyor") } },
    Unknown: { level: 'neutral', get label() { return tx("Full Backup durumu bilinmiyor") } },
  },
  databaseState: {
    Online: { level: 'safe', get label() { return tx("Çevrimiçi") } },
    Offline: { level: 'attention', get label() { return tx("Çevrimdışı") } },
    Unknown: { level: 'neutral', get label() { return tx("Bilinmiyor") } },
  },
  sourceStatus: {
    Scanned: { level: 'safe', get label() { return tx("Tarandı") } },
    Unavailable: { level: 'attention', get label() { return tx("Kullanılamıyor") } },
    Disabled: { level: 'neutral', get label() { return tx("Kapalı") } },
    Failed: { level: 'urgent', get label() { return tx("Başarısız") } },
  },
  scanOutcome: {
    Completed: { level: 'safe', get label() { return tx("Tamamlandı") } },
    Partial: { level: 'attention', get label() { return tx("Kısmen tamamlandı") } },
    Failed: { level: 'urgent', get label() { return tx("Başarısız") } },
  },
  scanState: {
    Idle: { level: 'neutral', get label() { return tx("Boşta") } },
    Requested: { level: 'pending', get label() { return tx("Tarama istendi") } },
    Running: { level: 'running', get label() { return tx("Taranıyor") } },
  },
  wrapKey: {
    Active: { level: 'safe', get label() { return tx("Etkin") } },
    DecryptOnly: { level: 'info', get label() { return tx("Yalnızca çözme") } },
    Retired: { level: 'neutral', get label() { return tx("Emekli") } },
  },
  breakGlass: {
    Pending: { level: 'attention', get label() { return tx("Onay bekliyor") } },
    Approved: { level: 'info', get label() { return tx("Onaylandı") } },
    Denied: { level: 'urgent', get label() { return tx("Reddedildi") } },
    Consumed: { level: 'safe', get label() { return tx("Kullanıldı") } },
    Expired: { level: 'neutral', get label() { return tx("Süresi doldu") } },
  },
  encryptionPolicy: {
    Off: { level: 'neutral', get label() { return tx("Kapalı") } },
    Optional: { level: 'safe', get label() { return tx("İsteğe bağlı") } },
    Required: { level: 'safe', get label() { return tx("Zorunlu") } },
  },
  subscription: {
    Active: { level: 'safe', get label() { return tx("Etkin") } },
    Suspended: { level: 'attention', get label() { return tx("Askıda") } },
    Expired: { level: 'urgent', get label() { return tx("Süresi doldu") } },
    Superseded: { level: 'neutral', get label() { return tx("Değiştirildi") } },
    NoSubscription: { level: 'neutral', get label() { return tx("Abonelik yok") } },
  },
  quota: {
    Normal: { level: 'safe', get label() { return tx("Normal") } },
    Warning: { level: 'attention', get label() { return tx("Warning") } },
    Critical: { level: 'urgent', get label() { return tx("Critical") } },
    OverQuota: { level: 'urgent', get label() { return tx("OverQuota") } },
    Unlimited: { level: 'info', get label() { return tx("Unlimited") } },
  },
  supportSession: {
    Active: { level: 'safe', get label() { return tx("Etkin") } },
    Expired: { level: 'attention', get label() { return tx("Süresi doldu") } },
    Closed: { level: 'neutral', get label() { return tx("Kapatıldı") } },
    Revoked: { level: 'urgent', get label() { return tx("Erişim geri alındı") } },
  },
  verification: {
    Restored: { level: 'safe', get label() { return tx("Başarılı · Restored") } },
    Failed: { level: 'urgent', get label() { return tx("Başarısız · Failed") } },
    NotRun: { level: 'neutral', get label() { return tx("Çalışmadı · NotRun") } },
  },
  connectionTest: {
    Succeeded: { level: 'safe', get label() { return tx("Başarılı") } },
    Failed: { level: 'urgent', get label() { return tx("Başarısız") } },
    NotTestable: { level: 'neutral', get label() { return tx("Test edilemez") } },
  },
  provider: {
    Available: { level: 'safe', get label() { return tx("Kullanılabilir") } },
    Planned: { level: 'neutral', get label() { return tx("Yakında") } },
  },
  ownership: {
    PlatformManaged: { level: 'info', get label() { return tx("Platform yönetimli") } },
    TenantManaged: { level: 'neutral', get label() { return tx("Tenant yönetimli") } },
  },
  delivery: {
    pending: { level: 'pending', get label() { return tx("Sırada") } },
    in_progress: { level: 'running', get label() { return tx("Gönderiliyor") } },
    sent: { level: 'safe', get label() { return tx("Gönderildi") } },
    failed: { level: 'urgent', get label() { return tx("Başarısız") } },
    suppressed: { level: 'attention', get label() { return tx("Bastırıldı") } },
    cancelled: { level: 'neutral', get label() { return tx("İptal edildi") } },
  },
  backupHealth: {
    Healthy: { level: 'safe', get label() { return tx("Sağlıklı") } },
    Degraded: { level: 'attention', get label() { return tx("Dikkat gerektiren") } },
    Critical: { level: 'urgent', get label() { return tx("Kritik") } },
  },
} as const satisfies Record<string, Record<string, StatusMeta>>

export type StatusDomain = keyof typeof dictionary

/** Level and label for a status. Anything the dictionary does not know is shown as itself, neutral, so nothing is hidden. */
export function statusMeta(domain: StatusDomain, status: string): StatusMeta {
  const entries: Record<string, StatusMeta> = dictionary[domain]
  return Object.hasOwn(entries, status) ? entries[status] : { level: 'neutral', label: status }
}

/** The one label-map helper: the plain-language label of a status, for filters and sentences. */
export function statusLabel(domain: StatusDomain, status: string): string {
  return statusMeta(domain, status).label
}
