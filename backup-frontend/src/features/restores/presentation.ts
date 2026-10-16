import { tx } from '@/lib/i18n'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import type { RestoreCleanup, RestoreMode, RestoreStatus, RestoreTargetState, VerificationOutcome } from './types'

export const restoreStatuses: readonly RestoreStatus[] = ['Pending', 'Leased', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'AgentLost']
export const restoreStatusLabels: Record<RestoreStatus, string> = {
  get Pending() { return tx("Bekliyor") }, get Leased() { return tx("Agent tarafından alındı") }, get Running() { return tx("Çalışıyor") }, get Succeeded() { return tx("Başarılı") },
  get Failed() { return tx("Başarısız") }, get Cancelled() { return tx("İptal edildi") }, get AgentLost() { return tx("Agent bağlantısı kayboldu") },
}
export function restoreStatusLabel(status: RestoreStatus): string { return restoreStatusLabels[status] }
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
const restoreFailures: Record<string, string> = {
  get ReplicaUnavailable() { return tx("Replica geri yükleme için kullanılamıyor.") }, get ArtifactTransferFailed() { return tx("Artifact aktarımı tamamlanamadı.") },
  get ChecksumMismatch() { return tx("Artifact checksum değeri eşleşmedi.") }, get BackupInvalid() { return tx("Backup doğrulaması başarısız oldu.") },
  get BackupTypeUnsupported() { return tx("Bu backup türü geri yükleme için desteklenmiyor.") }, get IncompatibleVersion() { return tx("Kaynak ve hedef Database motoru sürümleri uyumlu değil.") },
  get FileMappingFailed() { return tx("Database dosya eşlemesi oluşturulamadı.") }, get PreCheckFailed() { return tx("Geri yükleme ön kontrolleri başarısız oldu.") },
  get TargetDatabaseExists() { return tx("Hedef Database zaten var. Yeni Database modu üzerine yazmaz.") }, get TargetDatabaseNotFound() { return tx("Üzerine yazılacak hedef Database bulunamadı.") },
  get TargetInUse() { return tx("Hedef Database kullanımda.") }, get TargetProtected() { return tx("Korunan hedef Database değiştirilemez.") },
  get InsufficientDiskSpace() { return tx("Hedefte yeterli disk alanı yok.") }, get SqlRestoreFailed() { return tx("Database geri yükleme işlemi tamamlanamadı.") },
  get VerificationFailed() { return tx("Backup geri yüklendi ancak geri yüklenen Database bütünlük veya veri okuma kontrollerinden geçemedi.") },
  get confirmation_mismatch() { return tx("Yazdığınız Database adı eşleşmiyor.") }, get retention_not_elapsed() { return tx("Rollback kopyasının koruma süresi henüz dolmadı.") },
  get cleanup_not_applicable() { return tx("Bu işlem için temizlik şu anda uygulanamaz.") },
  get NotClaimed() { return tx("Restore süresi içinde Agent tarafından alınmadı. Hedef değiştirilmedi.") },
  get precheck_rejected() { return tx("Ön kontrol bu geri yükleme ile eşleşmiyor veya başarılı değil. Ön kontrolü yeniden çalıştırın.") },
  get agent_transfer_required() { return tx("Replica başka bir Agent üzerinde. Agentlar arası aktarım desteklenmiyor.") },
  get no_accessible_replica() { return tx("Bu Agent tarafından erişilebilen Replica yok.") }, get replica_not_restorable() { return tx("Replica geri yüklemeye uygun değil.") },
  get replica_not_verified() { return tx("Replica doğrulanmadı.") }, get checksum_unavailable() { return tx("Artifact checksum bilgisi yok.") }, get backup_type_unsupported() { return tx("Backup türü desteklenmiyor.") },
  get overwrite_confirmation_required() { return tx("Üzerine yazmayı hedef Database adıyla onaylayın.") },
}
export function restoreFailureMessage(code?: string | null): string { return code ? restoreFailures[code] ?? failureMessage(code) : failureMessage(code) }
export function restoreErrorMessage(error: unknown): string {
  if (error instanceof ApiError && error.status !== 401 && error.status !== 403 && error.status !== 404 && error.status < 500) {
    return restoreFailureMessage(error.reason ?? error.code)
  }
  return errorMessage(error)
}
export function safeRestoreCode(code?: string | null): string { return code && /^[A-Za-z0-9_.-]{1,80}$/.test(code) ? code : '—' }
export function databaseStateLabel(state?: string | null): string {
  const known = [tx('ONLINE'), tx('RESTORING'), tx('RECOVERING'), tx('RECOVERY_PENDING'), tx('SUSPECT'), tx('EMERGENCY'), tx('OFFLINE'), tx('COPYING'), tx('OFFLINE_SECONDARY')]
  return state && known.includes(state.toUpperCase()) ? state.toUpperCase() : tx('Bilinmiyor')
}
export function restoreProgressPercent(value?: number | null): number | undefined {
  return value != null && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : undefined
}
export const verificationOutcomeLabels: Record<VerificationOutcome, string> = {
  get Restored() { return tx("Başarılı · Restored") }, get Failed() { return tx("Başarısız · Failed") }, get NotRun() { return tx("Çalışmadı · NotRun") },
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
