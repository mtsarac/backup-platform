import type { AgentCapability } from '@/features/agents/types'
import type { DiscoveryAgentStatus, DiscoveryDatabase, DiscoveryInstanceSummary } from './types'

export const DISCOVERY_CAPABILITY = 'discovery.database-inventory'
export const SCAN_POLL_MS = 3000
export type Tone = 'neutral' | 'success' | 'warning' | 'error' | 'info'
type Meta = { label: string; tone: Tone }

export const reachabilityMeta: Record<string, Meta> = {
  Reachable: { label: 'Erişilebilir', tone: 'success' },
  Unreachable: { label: 'Erişilemiyor', tone: 'error' },
  Unknown: { label: 'Bilinmiyor', tone: 'neutral' },
}
export const accessMeta: Record<string, Meta> = {
  CredentialsRequired: { label: 'Kimlik bilgisi gerekli', tone: 'warning' },
  Authenticated: { label: 'Kimlik doğrulandı', tone: 'success' },
  AuthenticationFailed: { label: 'Kimlik doğrulaması başarısız', tone: 'error' },
  Unreachable: { label: 'Bağlanılamadı', tone: 'error' },
  Error: { label: 'Hata', tone: 'error' },
  Unknown: { label: 'Bilinmiyor', tone: 'neutral' },
}
export const presenceMeta: Record<string, Meta> = {
  Present: { label: 'Mevcut', tone: 'success' },
  Missing: { label: 'Kayıp', tone: 'neutral' },
}
export const lastChangeMeta: Record<string, Meta> = {
  Discovered: { label: 'Yeni', tone: 'info' },
  Changed: { label: 'Değişti', tone: 'warning' },
  Recovered: { label: 'Yeniden erişilebilir', tone: 'success' },
  Unreachable: { label: 'Erişilemiyor', tone: 'error' },
  Missing: { label: 'Kayboldu', tone: 'neutral' },
  Reappeared: { label: 'Yeniden göründü', tone: 'info' },
}
export const fullBackupMeta: Record<string, Meta> = {
  Supported: { label: 'Full Backup destekleniyor', tone: 'success' },
  Unsupported: { label: 'Full Backup desteklenmiyor', tone: 'error' },
  Unknown: { label: 'Full Backup durumu bilinmiyor', tone: 'neutral' },
}
export const databaseStateMeta: Record<string, Meta> = {
  Online: { label: 'Çevrimiçi', tone: 'success' },
  Offline: { label: 'Çevrimdışı', tone: 'warning' },
  Unknown: { label: 'Bilinmiyor', tone: 'neutral' },
}
export const sourceLabels: Record<string, string> = { Service: 'Servis', Listener: 'Dinleyici (port)', Docker: 'Docker' }
export const sourceStatusMeta: Record<string, Meta> = {
  Scanned: { label: 'Tarandı', tone: 'success' },
  Unavailable: { label: 'Kullanılamıyor', tone: 'warning' },
  Disabled: { label: 'Kapalı', tone: 'neutral' },
  Failed: { label: 'Başarısız', tone: 'error' },
}
export const outcomeMeta: Record<string, Meta> = {
  Completed: { label: 'Tamamlandı', tone: 'success' },
  Partial: { label: 'Kısmen tamamlandı', tone: 'warning' },
  Failed: { label: 'Başarısız', tone: 'error' },
}
export const scanStateMeta: Record<string, Meta> = {
  Idle: { label: 'Boşta', tone: 'neutral' },
  Requested: { label: 'Tarama istendi', tone: 'info' },
  Running: { label: 'Taranıyor', tone: 'info' },
}
export const engineLabels: Record<string, string> = { mssql: 'MSSQL', postgres: 'PostgreSQL' }

// Reason codes are agent-produced fixed codes; unknown ones are shown as the bare code, never as free text.
const reasonLabels: Record<string, string> = {
  docker_not_found: 'Docker CLI bulunamadı.',
  docker_access_denied: 'Docker CLI için yetki yok.',
  docker_timeout: 'Docker yanıt vermedi (zaman aşımı).',
  docker_failed: 'Docker sorgulanamadı (servis çalışmıyor olabilir).',
  disabled_by_settings: 'Ayarlarda kapatıldı.',
  systemd_not_found: 'systemd bulunamadı.',
  systemd_not_running: 'systemd çalışmıyor.',
  systemd_access_denied: 'systemd için yetki yok.',
  systemd_failed: 'systemd sorgulanamadı.',
  registry_access_denied: 'Registry için yetki yok.',
  registry_failed: 'Registry okunamadı.',
  detector_failed: 'Bu kaynak okunamadı.',
  probe_failed: 'Port yoklaması başarısız oldu.',
  scan_timeout: 'Tarama süre sınırını aştı.',
  scan_failed: 'Tarama başarısız oldu.',
  timeout: 'Zaman aşımına uğradı.',
  no_published_port: 'Container portu dışarı açılmamış; bağlanılamaz.',
  no_endpoint: 'Bağlanılacak adres bulunamadı.',
  too_many_databases: 'Veritabanı listesi tek raporda taşınamayacak kadar büyük.',
  credential_unavailable: 'Kimlik bilgisi referansı Agent üzerinde çözülemedi.',
  provider_unavailable: 'Bu motor için sağlayıcı yok.',
  invalid_configuration: 'Bağlantı ayarı geçersiz.',
  permission_denied: 'Okuma yetkisi yok.',
  authentication_failed: 'Kimlik doğrulaması başarısız.',
  unreachable: 'Sunucuya bağlanılamadı.',
  connection_failed: 'Sunucu yanıt veriyor ancak oturum açılamadı (şifreleme ayarını kontrol edin).',
  unexpected: 'Beklenmeyen hata.',
  credentials_required: 'Kimlik bilgisi gerekli.',
  not_supported: 'Desteklenmiyor.',
  'system-database-tempdb': 'tempdb yedeklenemez.',
  'system-database-master-full-only': 'master yalnızca Full Backup ile yedeklenir.',
  'connections-disallowed': 'Veritabanı bağlantıya kapalı (şablon veritabanı).',
}
export function reasonText(code: string | null | undefined) {
  if (!code) return null
  return reasonLabels[code] ?? (/^[A-Za-z0-9_.-]{1,80}$/.test(code) ? `Neden kodu: ${code}` : null)
}

export const supportsDiscovery = (capabilities: readonly AgentCapability[] | undefined) => Boolean(capabilities?.some(item => item.identifier === DISCOVERY_CAPABILITY && item.support === 'Supported'))
export const isScanActive = (items: readonly Pick<DiscoveryAgentStatus, 'scanState'>[] | undefined) => Boolean(items?.some(item => item.scanState !== 'Idle'))

/** A job may be created from an instance only when the agent really enumerated its databases and has an address to connect to. */
export const canCreateJob = (instance: Pick<DiscoveryInstanceSummary, 'presence' | 'access' | 'host' | 'port'>) =>
  instance.presence === 'Present' && instance.access === 'Authenticated' && Boolean(instance.host) && instance.port != null
export const databaseSelectable = (database: DiscoveryDatabase, instance: Pick<DiscoveryInstanceSummary, 'presence' | 'access' | 'host' | 'port'>) =>
  canCreateJob(instance) && !database.isSystem && database.presence === 'Present' && database.state === 'Online' && database.fullBackup !== 'Unsupported' && database.jobs.length === 0

/** Why a database cannot be picked; null when it can. */
export function databaseBlockedReason(database: DiscoveryDatabase, instance: Pick<DiscoveryInstanceSummary, 'presence' | 'access' | 'host' | 'port'>) {
  if (databaseSelectable(database, instance)) return null
  if (!canCreateJob(instance)) return 'Instance için Backup Job oluşturulamaz.'
  if (database.isSystem) return 'Sistem veritabanı.'
  if (database.presence !== 'Present') return 'Veritabanı son taramada görülmedi.'
  if (database.state !== 'Online') return 'Veritabanı çevrimiçi değil.'
  if (database.fullBackup === 'Unsupported') return reasonText(database.fullBackupReasonCode) ?? 'Full Backup desteklenmiyor.'
  if (database.jobs.length > 0) return 'Zaten bir Backup Job kapsıyor.'
  return null
}
