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
  Succeeded: { label: 'Başarılı', tone: 'success' },
  Failed: { label: 'Başarısız', tone: 'error' },
  NotTestable: { label: 'Test edilemez', tone: 'neutral' },
}

export const availabilityMeta: Record<ProviderAvailability, { label: string; tone: Tone }> = {
  Available: { label: 'Kullanılabilir', tone: 'success' },
  Planned: { label: 'Yakında', tone: 'neutral' },
}

const capabilityLabels: Record<string, string> = {
  'storage.read': 'Okuma',
  'storage.delete': 'Silme',
  'storage.atomic-publish': 'Atomik yayımlama',
  'storage.enumerate': 'Listeleme',
  'storage.verify-absence': 'Silme doğrulama',
  'storage.server-side-checksum': 'Sunucu tarafı checksum',
  'storage.capacity-query': 'Kapasite sorgusu',
  'storage.resume': 'Kaldığı yerden devam',
  'storage.multipart': 'Çok parçalı yükleme',
  'storage.immutable': 'Değişmezlik (immutable)',
}

const testMessages: Record<string, string> = {
  ok: 'Yazma, doğrulama ve silme sınaması başarılı.',
  agent_side_storage: 'Backend Agent diskine erişemez; bu hedef backup çalışırken Agent tarafından denetlenir.',
  storage_not_configured: 'Depolama yapılandırılmamış.',
  provider_not_implemented: 'Bu Storage Provider henüz uygulanmadı; test edilemez.',
  secret_unavailable: 'Kimlik bilgisi kullanılamıyor; kimlik bilgisini yeniden ayarlayın.',
  access_denied: 'Depolama erişim izni reddedildi.',
  invalid_configuration: 'Depolama yapılandırması geçersiz.',
  insufficient_space: 'Depolamada yeterli boş alan yok.',
  integrity_mismatch: 'Yazılan veri doğrulanamadı (checksum uyuşmuyor).',
  not_supported: 'Depolama bu işlemi desteklemiyor.',
  io_error: 'Depolama dosya işlemi tamamlanamadı.',
  unexpected: 'Test beklenmeyen bir nedenle tamamlanamadı.',
  host_key_mismatch: 'SFTP sunucu anahtarı parmak izi uyuşmuyor. Güvenilir kanaldan doğrulamadan değiştirmeyin.',
  authentication_failed: 'SFTP kimlik doğrulaması başarısız. Kullanıcı ve yeni kimlik bilgisini kontrol edin.',
  timeout: 'Depolama bağlantısı zaman aşımına uğradı.',
  network_denied: 'Depolama sunucusuna erişim ağ güvenlik politikası tarafından engellendi.',
  network_unavailable: 'Depolama sunucusuna ağ üzerinden ulaşılamıyor.',
}

/** The backend's `message` is a fixed English sentence: show Turkish by stable `code`, and the backend text only for a code the UI does not know. */
export function testMessage(test: Pick<ConnectionTest, 'code' | 'message'>): string {
  return test.code && Object.hasOwn(testMessages, test.code) ? testMessages[test.code] : test.message
}

/** Known capability identifiers get a Turkish word; anything else is shown by its raw identifier so nothing is hidden. */
export function capabilityLabel(capability: string): string {
  return Object.hasOwn(capabilityLabels, capability) ? capabilityLabels[capability] : capability
}

const topologyLabels: Record<ProviderTopology, string> = { AgentDirect: 'Agent doğrudan yazar', BackendRelayed: 'Backend üzerinden aktarılır' }
const executionLocationLabels: Record<ExecutionLocation, string> = { Agent: 'Agent', Backend: 'Backend' }
const testModeLabels: Record<TestMode, string> = {
  BackendRoundTrip: 'Backend yazma / doğrulama / silme sınaması',
  AgentSide: 'Agent tarafında; backend erişemez, sonuç Test edilemez olur',
  None: 'Test desteklenmiyor',
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
  return connection.hasSecret ? SECRET_PRESENT_LABEL : SECRET_ABSENT_LABEL
}

function configurationValue(value: ConfigurationValue) {
  if (value === null || value === '') return '—'
  if (typeof value === 'boolean') return value ? 'Evet' : 'Hayır'
  return String(value)
}

/** Label/value pairs of the safe configuration, labelled from the descriptor when it is known. Never touches secrets: the API carries none. */
export function configurationSummary(connection: Pick<ConnectionView, 'configuration'>, descriptor?: Pick<ProviderDescriptor, 'configFields'>) {
  return Object.entries(connection.configuration ?? {}).map(([name, value]) => ({
    name,
    label: descriptor?.configFields.find((field) => field.name === name)?.label ?? name,
    value: configurationValue(value),
  }))
}

/** Tenant creation is offered only for an Available provider the tenant may create and while it is below the per-tenant limit. */
export function tenantCreateState(provider: ProviderDescriptor, connections: readonly ConnectionView[]): { creatable: boolean; reason: string | null } {
  if (provider.availability !== 'Available') return { creatable: false, reason: 'Yakında / henüz kullanılamaz' }
  if (!provider.tenantCreatable) return { creatable: false, reason: 'Platform tarafından yönetilir; tenant bağlantı ekleyemez' }
  const count = connections.filter((connection) => connection.providerType === provider.providerType).length
  if (provider.maxConnectionsPerTenant !== null && count >= provider.maxConnectionsPerTenant) return { creatable: false, reason: `Bağlantı sınırına ulaşıldı (${provider.maxConnectionsPerTenant})` }
  return { creatable: true, reason: null }
}

export function isPlatformCreatable(provider: ProviderDescriptor) {
  return provider.availability === 'Available' && provider.platformCreatable
}

/** The connection routes reuse `concurrent_update`, which the shared mapper words for plans. */
export function connectionErrorMessage(error: unknown): string {
  if (error instanceof ApiError && (error.reason ?? error.code) === 'concurrent_update') return 'Bağlantı başka bir işlemle aynı anda değiştirildi. Sayfayı yenileyip tekrar deneyin.'
  if (error instanceof ApiError && (error.reason ?? error.code) === 'rotation_test_failed') return 'Yeni kimlik bilgisi veya sunucu parmak izi doğrulanamadı. Mevcut bağlantı ve kimlik bilgisi korunuyor.'
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
