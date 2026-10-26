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
  get credential_unavailable() { return tx("Kayıtlı kimlik bilgisi bilgisayarda bulunamadı.") },
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
  get 'system-database-master-full-only'() { return tx("master yalnızca tam yedek ile yedeklenir.") },
  get 'connections-disallowed'() { return tx("Veritabanı bağlantıya kapalı (şablon veritabanı).") },
}
/** Only reasons the agent is known to send; an unknown code has no plain wording, so it stays out of body text. */
export const plainReasonText = (code: string | null | undefined) => (code ? reasonLabels[code] ?? null : null)
/** Technical copy includes the raw code even when we have a plain explanation for it. */
export function reasonText(code: string | null | undefined) {
  if (!code || !/^[A-Za-z0-9_.-]{1,80}$/.test(code)) return null
  return [code, plainReasonText(code)].filter(Boolean).join(' · ')
}

export const supportsDiscovery = (capabilities: readonly AgentCapability[] | undefined) => Boolean(capabilities?.some(item => item.identifier === DISCOVERY_CAPABILITY && item.support === 'Supported'))
export const isScanActive = (items: readonly Pick<DiscoveryAgentStatus, 'scanState'>[] | undefined) => Boolean(items?.some(item => item.scanState !== 'Idle'))

/** A job may be created from an instance only when the agent really enumerated its databases and has an address to connect to. */
export const canCreateJob = (instance: Pick<DiscoveryInstanceSummary, 'presence' | 'access' | 'host' | 'port'>) =>
  instance.presence === 'Present' && instance.access === 'Authenticated' && Boolean(instance.host) && instance.port != null
export const databaseSelectable = (database: DiscoveryDatabase, instance: Pick<DiscoveryInstanceSummary, 'presence' | 'access' | 'host' | 'port'>) =>
  canCreateJob(instance) && !database.isSystem && database.presence === 'Present' && database.state === 'Online' && database.fullBackup !== 'Unsupported' && database.jobs.length === 0

export type DatabaseAvailability = { kind: 'available' | 'covered' | 'unavailable'; reason: string | null }

/** Whether a database can be backed up, and in plain words why not. `covered` means it could be, but a backup job already protects it. */
export function databaseAvailability(database: DiscoveryDatabase, instance: Pick<DiscoveryInstanceSummary, 'presence' | 'access' | 'host' | 'port'>): DatabaseAvailability {
  if (databaseSelectable(database, instance)) return { kind: 'available', reason: null }
  if (!canCreateJob(instance)) return { kind: 'unavailable', reason: tx('Bu veritabanı sunucusuna henüz bağlanılamadığı için yedekleme işi oluşturulamıyor.') }
  if (database.isSystem) return { kind: 'unavailable', reason: tx('Sistem veritabanı; yedeklenmez.') }
  if (database.presence !== 'Present') return { kind: 'unavailable', reason: tx('Son aramada bulunamadı.') }
  if (database.state !== 'Online') return { kind: 'unavailable', reason: tx('Veritabanı çevrimiçi değil.') }
  if (database.fullBackup === 'Unsupported') return { kind: 'unavailable', reason: plainReasonText(database.fullBackupReasonCode) ?? tx('Bu veritabanı tam yedeği desteklemiyor.') }
  return { kind: 'covered', reason: tx('Zaten bir yedekleme işi koruyor.') }
}
