import { tx } from '@/lib/i18n'
import type { AgentCapability } from '@/features/agents/types'
import type { DiscoveryAgentStatus, DiscoveryDatabase, DiscoveryInstanceSummary } from './types'

export const DISCOVERY_CAPABILITY = 'discovery.database-inventory'
export const SCAN_POLL_MS = 3000
export const sourceLabels: Record<string, string> = { get Service() { return tx("Servis") }, get Listener() { return tx("Dinleyici (port)") }, Docker: 'Docker' }
export const engineLabels: Record<string, string> = { mssql: 'MSSQL', postgres: 'PostgreSQL' }

// Reason codes are agent-produced fixed codes; unknown ones are shown as the bare code, never as free text.
const reasonLabels: Record<string, string> = {
  get docker_not_found() { return tx("Docker CLI bulunamadı.") },
  get docker_access_denied() { return tx("Docker CLI için yetki yok.") },
  get docker_timeout() { return tx("Docker yanıt vermedi (zaman aşımı).") },
  get docker_failed() { return tx("Docker sorgulanamadı (servis çalışmıyor olabilir).") },
  get disabled_by_settings() { return tx("Ayarlarda kapatıldı.") },
  get systemd_not_found() { return tx("systemd bulunamadı.") },
  get systemd_not_running() { return tx("systemd çalışmıyor.") },
  get systemd_access_denied() { return tx("systemd için yetki yok.") },
  get systemd_failed() { return tx("systemd sorgulanamadı.") },
  get registry_access_denied() { return tx("Registry için yetki yok.") },
  get registry_failed() { return tx("Registry okunamadı.") },
  get detector_failed() { return tx("Bu kaynak okunamadı.") },
  get probe_failed() { return tx("Port yoklaması başarısız oldu.") },
  get scan_timeout() { return tx("Tarama süre sınırını aştı.") },
  get scan_failed() { return tx("Tarama başarısız oldu.") },
  get timeout() { return tx("Zaman aşımına uğradı.") },
  get no_published_port() { return tx("Container portu dışarı açılmamış; bağlanılamaz.") },
  get no_endpoint() { return tx("Bağlanılacak adres bulunamadı.") },
  get too_many_databases() { return tx("Veritabanı listesi tek raporda taşınamayacak kadar büyük.") },
  get credential_unavailable() { return tx("Kimlik bilgisi referansı Agent üzerinde çözülemedi.") },
  get provider_unavailable() { return tx("Bu motor için sağlayıcı yok.") },
  get invalid_configuration() { return tx("Bağlantı ayarı geçersiz.") },
  get permission_denied() { return tx("Okuma yetkisi yok.") },
  get authentication_failed() { return tx("Kimlik doğrulaması başarısız.") },
  get unreachable() { return tx("Sunucuya bağlanılamadı.") },
  get connection_failed() { return tx("Sunucu yanıt veriyor ancak oturum açılamadı (şifreleme ayarını kontrol edin).") },
  get unexpected() { return tx("Beklenmeyen hata.") },
  get credentials_required() { return tx("Kimlik bilgisi gerekli.") },
  get not_supported() { return tx("Desteklenmiyor.") },
  get 'system-database-tempdb'() { return tx("tempdb yedeklenemez.") },
  get 'system-database-master-full-only'() { return tx("master yalnızca Full Backup ile yedeklenir.") },
  get 'connections-disallowed'() { return tx("Veritabanı bağlantıya kapalı (şablon veritabanı).") },
}
export function reasonText(code: string | null | undefined) {
  if (!code) return null
  return reasonLabels[code] ?? (/^[A-Za-z0-9_.-]{1,80}$/.test(code) ? tx("Neden kodu: {0}", { "0": code }) : null)
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
  if (!canCreateJob(instance)) return tx('Instance için Backup Job oluşturulamaz.')
  if (database.isSystem) return tx('Sistem veritabanı.')
  if (database.presence !== 'Present') return tx('Veritabanı son taramada görülmedi.')
  if (database.state !== 'Online') return tx('Veritabanı çevrimiçi değil.')
  if (database.fullBackup === 'Unsupported') return reasonText(database.fullBackupReasonCode) ?? tx('Full Backup desteklenmiyor.')
  if (database.jobs.length > 0) return tx('Zaten bir Backup Job kapsıyor.')
  return null
}
