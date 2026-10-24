import { tx } from '@/lib/i18n'
import { ApiError } from '@/lib/api/client'
import type { RestoreCleanup, RestoreMode, RestoreStatus, RestoreTargetState } from './types'

export const restoreStatuses: readonly RestoreStatus[] = ['Pending', 'Leased', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'AgentLost']
export function isActiveRestore(status: string): boolean { return ['Pending', 'Leased', 'Running'].includes(status) }
export function canCancelRestore(status: string, cancellationRequestedAt?: string | null): boolean { return isActiveRestore(status) && !cancellationRequestedAt }
export function restoreModeLabel(mode: RestoreMode): string { return mode === 'NewDatabase' ? tx('Yeni Database') : tx('Mevcut Database üzerine yaz') }
export function targetStateLabel(state?: RestoreTargetState | null): string {
  return state ? ({ NotModified: tx('Değiştirilmedi · NotModified'), Online: tx('Çevrimiçi · Online'), PartiallyRestored: tx('Kısmen geri yüklendi · PartiallyRestored'), Unknown: tx('Bilinmiyor · Unknown') })[state] : tx('Henüz bildirilmedi')
}
export function targetStateDescription(state?: RestoreTargetState | null): string {
  switch (state) {
    case 'NotModified': return tx('Hedef Database değiştirilmedi. Bu bilgi Database sağlığının kontrol edildiği anlamına gelmez.')
    case 'Online': return tx('Agent, geri yüklenen hedef Database durumunu Online olarak bildirdi.')
    case 'PartiallyRestored': return tx('Hedef değiştirildi fakat geri yükleme tamamlanmadı. Database RESTORING durumunda, hasarlı veya eksik olabilir; üzerine yazılan eski içerik kaybolmuş olabilir. Elle müdahale gerekebilir.')
    case 'Unknown': return tx('Agent hedef Database durumunu belirleyemedi. Database sağlıklı kabul edilmemelidir; elle kontrol ve müdahale gerekebilir.')
    default: return tx('Hedef Database durumu henüz bildirilmedi. İşlem durumundan Database sağlığı çıkarılamaz.')
  }
}
export function restoreStageLabel(stage?: string | null): string {
  const labels: Record<string, string> = {
    Preparing: tx('Hazırlanıyor'), DownloadingArtifact: tx('Artifact aktarılıyor'), VerifyingChecksum: tx('Checksum doğrulanıyor'),
    InspectingBackup: tx('Backup inceleniyor'), PreparingTarget: tx('Hedef hazırlanıyor'), Restoring: tx('Geri yükleniyor'),
    Finalizing: tx('Tamamlanıyor'), Verifying: tx('Geri yüklenen Database doğrulanıyor'), CleaningUp: tx('Geçici Database kaldırılıyor'), Completed: tx('Tamamlandı'), Failed: tx('Başarısız'), Cancelled: tx('İptal edildi'),
  }
  return stage ? labels[stage] ?? tx('Bildirilen aşama tanınmıyor') : tx('Henüz ilerleme bildirilmedi')
}
export function safeRestoreCode(code?: string | null): string { return code && /^[A-Za-z0-9_.-]{1,80}$/.test(code) ? code : '—' }
export function databaseStateLabel(state?: string | null): string {
  const known = [tx('ONLINE'), tx('RESTORING'), tx('RECOVERING'), tx('RECOVERY_PENDING'), tx('SUSPECT'), tx('EMERGENCY'), tx('OFFLINE'), tx('COPYING'), tx('OFFLINE_SECONDARY')]
  return state && known.includes(state.toUpperCase()) ? state.toUpperCase() : tx('Bilinmiyor')
}
export function restoreProgressPercent(value?: number | null): number | undefined {
  return value != null && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : undefined
}
export function verificationCheckLabel(checkId: string): string {
  const labels: Record<string, string> = { 'database-online': tx('Database çevrimiçi'), integrity: tx('Bütünlük kontrolü'), 'data-readable': tx('Veri okunabilir') }
  return labels[checkId] ?? safeRestoreCode(checkId)
}
export function verificationCleanupText(cleanup: RestoreCleanup): string {
  if (cleanup.state === 'Completed') return tx('Geçici Database kaldırıldı')
  if (cleanup.needsAttention) return tx('Kaldırma dikkat gerektiriyor')
  return tx("Kaldırma bekliyor, otomatik yeniden denenecek (deneme {0})", { "0": cleanup.attempts })
}
/** What a pre-check is about, in everyday words. Ids come from the engines and are an open set, so unknown ones get a generic name; the raw id lives in the technical details. */
export function precheckLabel(checkId: string): string {
  const labels: Record<string, string> = {
    'server.reachable': tx('Sunucuya ulaşılabiliyor'), 'server.authentication': tx('Sunucu girişi kabul ediliyor'), 'server.instance': tx('Doğru sunucu örneğine bağlanılıyor'),
    'database.exists': tx('Hedef veritabanı durumu'), 'database.state': tx('Veritabanı çalışır durumda'), 'permission.restore': tx('Geri yükleme izni var'),
    'restore.target-server': tx('Hedef sunucu bilgisi okunabiliyor'), 'restore.database-name': tx('Veritabanı adı uygun'), 'restore.target-protected': tx('Hedef korunan bir veritabanı değil'),
    'restore.target-sessions': tx('Hedefte açık bağlantı yok'), 'restore.target-directories': tx('Hedef klasörler uygun'), 'restore.disk-space': tx('Yeterli disk alanı var'),
    'restore.backup-type': tx('Yedek türü destekleniyor'), 'restore.version-compatibility': tx('Sunucu sürümleri uyumlu'), 'restore.edition-compatibility': tx('Sunucu sürüm türü uyumlu'),
    'restore.target-relation': tx('Hedef, yedeğin kaynağıyla uyumlu'), 'restore.file-types': tx('Dosya türleri destekleniyor'), 'restore.file-mapping': tx('Dosya yerleşimi hazır'),
    'staging.agent-access': tx('Geçici klasöre yazılabiliyor'), 'staging.disk-space': tx('Geçici klasörde yeterli alan var'), 'staging.path-format': tx('Geçici klasör yolu geçerli'),
    'staging.server-writable': tx('Sunucu geçici klasöre yazabiliyor'), 'staging.mapping': tx('Geçici klasör sunucuyla paylaşılıyor'),
  }
  return Object.hasOwn(labels, checkId) ? labels[checkId] : tx('Diğer kontrol')
}
/**
 * How a failed start reads to the user. A failure that is not an API answer leaves it unknown whether the restore began; retrying is safe
 * because the same request key is reused.
 */
export function describeStartFailure(error: unknown) {
  if (error == null || error instanceof ApiError) return error ?? undefined
  return {
    what: tx('Geri yükleme başlatılamadı.'),
    dataImpact: tx('Geri yüklemenin başlayıp başlamadığını geri yükleme listesinden kontrol edin.'),
    action: tx('Aynı işlemi tekrar deneyin; yeniden denemeler ikinci bir geri yükleme oluşturmaz.'),
  }
}
