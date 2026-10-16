import { tx } from '@/lib/i18n'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import type {
  ConfigurationValue, ConnectionOwnership, ConnectionTest, ConnectionView, ExecutionLocation, ProviderAvailability, ProviderDescriptor, ProviderTopology, TestMode,
  TestStatus,
} from './types'

type Tone = 'neutral' | 'success' | 'warning' | 'error' | 'info'

export const ownershipTone: Record<ConnectionOwnership, Tone> = { PlatformManaged: 'info', TenantManaged: 'neutral' }

/** NotTestable is never green: the backend could not check the destination, which says nothing about whether it works. */
export const testStatusMeta: Record<TestStatus, { label: string; tone: Tone }> = {
  Succeeded: { get label() { return tx("Başarılı") }, tone: 'success' },
  Failed: { get label() { return tx("Başarısız") }, tone: 'error' },
  NotTestable: { get label() { return tx("Test edilemez") }, tone: 'neutral' },
}

export const availabilityMeta: Record<ProviderAvailability, { label: string; tone: Tone }> = {
  Available: { get label() { return tx("Kullanılabilir") }, tone: 'success' },
  Planned: { get label() { return tx("Yakında") }, tone: 'neutral' },
}

const capabilityLabels: Record<string, string> = {
  get 'storage.read'() { return tx("Okuma") },
  get 'storage.delete'() { return tx("Silme") },
  get 'storage.atomic-publish'() { return tx("Atomik yayımlama") },
  get 'storage.enumerate'() { return tx("Listeleme") },
  get 'storage.verify-absence'() { return tx("Silme doğrulama") },
  get 'storage.server-side-checksum'() { return tx("Sunucu tarafı checksum") },
  get 'storage.capacity-query'() { return tx("Kapasite sorgusu") },
  get 'storage.resume'() { return tx("Kaldığı yerden devam") },
  get 'storage.multipart'() { return tx("Çok parçalı yükleme") },
  get 'storage.immutable'() { return tx("Değişmezlik (immutable)") },
}

const testMessages: Record<string, string> = {
  get ok() { return tx("Yazma, doğrulama ve silme sınaması başarılı.") },
  get agent_side_storage() { return tx("Backend Agent diskine erişemez; bu hedef backup çalışırken Agent tarafından denetlenir.") },
  get storage_not_configured() { return tx("Depolama yapılandırılmamış.") },
  get provider_not_implemented() { return tx("Bu Storage Provider henüz uygulanmadı; test edilemez.") },
  get secret_unavailable() { return tx("Kimlik bilgisi kullanılamıyor; kimlik bilgisini yeniden ayarlayın.") },
  get access_denied() { return tx("Depolama erişim izni reddedildi.") },
  get invalid_configuration() { return tx("Depolama yapılandırması geçersiz.") },
  get insufficient_space() { return tx("Depolamada yeterli boş alan yok.") },
  get integrity_mismatch() { return tx("Yazılan veri doğrulanamadı (checksum uyuşmuyor).") },
  get not_supported() { return tx("Depolama bu işlemi desteklemiyor.") },
  get io_error() { return tx("Depolama dosya işlemi tamamlanamadı.") },
  get unexpected() { return tx("Test beklenmeyen bir nedenle tamamlanamadı.") },
  get host_key_mismatch() { return tx("SFTP sunucu anahtarı parmak izi uyuşmuyor. Güvenilir kanaldan doğrulamadan değiştirmeyin.") },
  get authentication_failed() { return tx("SFTP kimlik doğrulaması başarısız. Kullanıcı ve yeni kimlik bilgisini kontrol edin.") },
  get timeout() { return tx("Depolama bağlantısı zaman aşımına uğradı.") },
  get network_denied() { return tx("Depolama sunucusuna erişim ağ güvenlik politikası tarafından engellendi.") },
  get network_unavailable() { return tx("Depolama sunucusuna ağ üzerinden ulaşılamıyor.") },
}

/** The backend's `message` is a fixed English sentence: show Turkish by stable `code`, and the backend text only for a code the UI does not know. */
export function testMessage(test: Pick<ConnectionTest, 'code' | 'message'>): string {
  return test.code && Object.hasOwn(testMessages, test.code) ? testMessages[test.code] : (test.message || tx('Unknown storage test result.'))
}

/** Known capability identifiers get a Turkish word; anything else is shown by its raw identifier so nothing is hidden. */
export function capabilityLabel(capability: string): string {
  return Object.hasOwn(capabilityLabels, capability) ? capabilityLabels[capability] : capability
}

export type Health = { key: 'disabled' | 'reauthorize' | 'failed' | 'untested' | 'agent-side' | 'healthy'; label: string; tone: Tone }

/**
 * One at-a-glance state for a destination, strongest problem first. It only restates what the API reported (enablement, the last test,
 * the Google re-authorization flag): a destination is never called healthy without a successful test.
 */
export function connectionHealth(connection: Pick<ConnectionView, 'effectiveEnabled' | 'lastTest' | 'providerType' | 'hasSecret' | 'configuration'>): Health {
  if (!connection.effectiveEnabled) return { key: 'disabled', label: tx('Devre dışı'), tone: 'warning' }
  if (connection.providerType === 'google-drive' && (!connection.hasSecret || connection.configuration?.reauthorizationRequired === 'true')) return { key: 'reauthorize', label: tx('Yeniden yetkilendirme gerekli'), tone: 'warning' }
  const test = connection.lastTest
  if (!test) return { key: 'untested', label: tx('Test edilmedi'), tone: 'neutral' }
  if (test.status === 'Failed') return { key: 'failed', label: tx('Bağlantı sorunu'), tone: 'error' }
  if (test.status === 'NotTestable') return { key: 'agent-side', label: tx('Agent tarafında denetlenir'), tone: 'neutral' }
  return { key: 'healthy', label: tx('Sağlıklı'), tone: 'success' }
}

const topologyLabels: Record<ProviderTopology, string> = { get AgentDirect() { return tx("Agent doğrudan yazar") }, get BackendRelayed() { return tx("Backend üzerinden aktarılır") } }
const executionLocationLabels: Record<ExecutionLocation, string> = { get Agent() { return tx("Agent") }, get Backend() { return tx("Backend") } }
const testModeLabels: Record<TestMode, string> = {
  get BackendRoundTrip() { return tx("Backend yazma / doğrulama / silme sınaması") },
  get AgentSide() { return tx("Agent tarafında; backend erişemez, sonuç Test edilemez olur") },
  get None() { return tx("Test desteklenmiyor") },
}

export function topologyLabel(topology: ProviderTopology | null) {
  return topology ? topologyLabels[topology] : '—'
}

export function executionLocationLabel(location: ExecutionLocation | null) {
  return location ? executionLocationLabels[location] : '—'
}

export function testModeLabel(mode: TestMode) {
  return testModeLabels[mode]
}

/** The semantics of disabling, straight from the API contract: the confirm dialog and the platform page both state them. */
export const disableEffects: readonly string[] = [
  'Bu hedefi kullanan yeni backup\'lar (manuel ve zamanlanmış) başlatılamaz; zamanlanmış çalışmalar "AdmissionDenied" olarak kaydedilir.',
  'Zaten başlamış çalışmalar sürer ve Replica\'larını yayımlar.',
  'Mevcut Replica\'lar silinmez ve kullanılabilir kalır: indirme, geri yükleme, saklama süresine göre silme ve kota hesabı değişmeden devam eder.',
]

export const enableEffect = 'Etkinleştirilince bu hedefe yeni backup\'lar yeniden başlatılabilir.'

export const SECRET_PRESENT_LABEL = 'Kimlik bilgisi kayıtlı'
export const SECRET_ABSENT_LABEL = 'Yok'

export function secretStatusLabel(connection: Pick<ConnectionView, 'hasSecret'>) {
  return connection.hasSecret ? tx(SECRET_PRESENT_LABEL) : tx(SECRET_ABSENT_LABEL)
}

function configurationValue(value: ConfigurationValue, kind?: string) {
  if (value === null || value === '') return '—'
  if (kind === 'Boolean' || value === 'true' || value === 'false') return value === 'true' ? tx('Evet') : tx('Hayır')
  return String(value)
}

/** Label/value pairs of the safe configuration, labelled from the descriptor when it is known. Never touches secrets: the API carries none. */
export function configurationSummary(connection: Pick<ConnectionView, 'configuration'>, descriptor?: Pick<ProviderDescriptor, 'configFields'>) {
  return Object.entries(connection.configuration ?? {}).map(([name, value]) => {
    const field = descriptor?.configFields.find((f) => f.name === name)
    return {
      name,
      label: tx(field?.label) ?? name,
      value: configurationValue(value, field?.kind),
    }
  })
}


/** Tenant creation is offered only for an Available provider the tenant may create and while it is below the per-tenant limit. */
export function tenantCreateState(provider: ProviderDescriptor, connections: readonly ConnectionView[]): { creatable: boolean; reason: string | null } {
  if (provider.availability !== 'Available') return { creatable: false, reason: tx('Yakında / henüz kullanılamaz') }
  if (!provider.tenantCreatable) return { creatable: false, reason: tx('Platform tarafından yönetilir; tenant bağlantı ekleyemez') }
  const count = connections.filter((connection) => connection.providerType === provider.providerType).length
  if (provider.maxConnectionsPerTenant !== null && count >= provider.maxConnectionsPerTenant) return { creatable: false, reason: tx("Bağlantı sınırına ulaşıldı ({0})", { "0": provider.maxConnectionsPerTenant }) }
  return { creatable: true, reason: null }
}

export function isPlatformCreatable(provider: ProviderDescriptor) {
  return provider.availability === 'Available' && provider.platformCreatable
}

/** The connection routes reuse `concurrent_update`, which the shared mapper words for plans. */
export function connectionErrorMessage(error: unknown): string {
  if (error instanceof ApiError && (error.reason ?? error.code) === 'concurrent_update') return tx('Bağlantı başka bir işlemle aynı anda değiştirildi. Sayfayı yenileyip tekrar deneyin.')
  if (error instanceof ApiError && (error.reason ?? error.code) === 'rotation_test_failed') return tx('Yeni kimlik bilgisi veya sunucu parmak izi doğrulanamadı. Mevcut bağlantı ve kimlik bilgisi korunuyor.')
  return errorMessage(error)
}

/** The request field the backend rejected (`name`, `configuration.<field>`, `secret.<field>`, `quotaScope`...), when it named one. */
export function errorField(error: unknown): string | undefined {
  return error instanceof ApiError ? error.field : undefined
}

/** `configuration.<field>` / `secret.<field>` error field -> the bare field name, for aria-invalid on the matching input. */
export function invalidFieldName(error: unknown, prefix: 'configuration' | 'secret'): string | undefined {
  const field = errorField(error)
  return field?.startsWith(`${prefix}.`) ? field.slice(prefix.length + 1) : undefined
}
