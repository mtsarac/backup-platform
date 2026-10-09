import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import type { RestoreMode, RestoreStatus, RestoreTargetState } from './types'

export const restoreStatuses: readonly RestoreStatus[] = ['Pending', 'Leased', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'AgentLost']
export const restoreStatusLabels: Record<RestoreStatus, string> = {
  Pending: 'Bekliyor', Leased: 'Agent tarafından alındı', Running: 'Çalışıyor', Succeeded: 'Başarılı',
  Failed: 'Başarısız', Cancelled: 'İptal edildi', AgentLost: 'Agent bağlantısı kayboldu',
}
export function restoreStatusLabel(status: RestoreStatus): string { return restoreStatusLabels[status] }
export function isActiveRestore(status: string): boolean { return ['Pending', 'Leased', 'Running'].includes(status) }
export function canCancelRestore(status: string, cancellationRequestedAt?: string | null): boolean { return isActiveRestore(status) && !cancellationRequestedAt }
export function restoreModeLabel(mode: RestoreMode): string { return mode === 'NewDatabase' ? 'Yeni Database' : 'Mevcut Database üzerine yaz' }
export function targetStateLabel(state?: RestoreTargetState | null): string {
  return state ? ({ NotModified: 'Değiştirilmedi · NotModified', Online: 'Çevrimiçi · Online', PartiallyRestored: 'Kısmen geri yüklendi · PartiallyRestored', Unknown: 'Bilinmiyor · Unknown' })[state] : 'Henüz bildirilmedi'
}
export function targetStateDescription(state?: RestoreTargetState | null): string {
  switch (state) {
    case 'NotModified': return 'Hedef Database değiştirilmedi. Bu bilgi Database sağlığının kontrol edildiği anlamına gelmez.'
    case 'Online': return 'Agent, geri yüklenen hedef Database durumunu Online olarak bildirdi.'
    case 'PartiallyRestored': return 'Hedef değiştirildi fakat geri yükleme tamamlanmadı. Database RESTORING durumunda, hasarlı veya eksik olabilir; üzerine yazılan eski içerik kaybolmuş olabilir. Elle müdahale gerekebilir.'
    case 'Unknown': return 'Agent hedef Database durumunu belirleyemedi. Database sağlıklı kabul edilmemelidir; elle kontrol ve müdahale gerekebilir.'
    default: return 'Hedef Database durumu henüz bildirilmedi. İşlem durumundan Database sağlığı çıkarılamaz.'
  }
}
export function restoreStageLabel(stage?: string | null): string {
  const labels: Record<string, string> = {
    Preparing: 'Hazırlanıyor', DownloadingArtifact: 'Artifact aktarılıyor', VerifyingChecksum: 'Checksum doğrulanıyor',
    InspectingBackup: 'Backup inceleniyor', PreparingTarget: 'Hedef hazırlanıyor', Restoring: 'Geri yükleniyor',
    Finalizing: 'Tamamlanıyor', Completed: 'Tamamlandı', Failed: 'Başarısız', Cancelled: 'İptal edildi',
  }
  return stage ? labels[stage] ?? 'Bildirilen aşama tanınmıyor' : 'Henüz ilerleme bildirilmedi'
}
const restoreFailures: Record<string, string> = {
  ReplicaUnavailable: 'Replica geri yükleme için kullanılamıyor.', ArtifactTransferFailed: 'Artifact aktarımı tamamlanamadı.',
  ChecksumMismatch: 'Artifact checksum değeri eşleşmedi.', BackupInvalid: 'Backup doğrulaması başarısız oldu.',
  BackupTypeUnsupported: 'Bu backup türü geri yükleme için desteklenmiyor.', IncompatibleVersion: 'Kaynak ve hedef SQL Server sürümleri uyumlu değil.',
  FileMappingFailed: 'Database dosya eşlemesi oluşturulamadı.', PreCheckFailed: 'Geri yükleme ön kontrolleri başarısız oldu.',
  TargetDatabaseExists: 'Hedef Database zaten var. Yeni Database modu üzerine yazmaz.', TargetDatabaseNotFound: 'Üzerine yazılacak hedef Database bulunamadı.',
  TargetInUse: 'Hedef Database kullanımda.', TargetProtected: 'Korunan hedef Database değiştirilemez.',
  InsufficientDiskSpace: 'Hedefte yeterli disk alanı yok.', SqlRestoreFailed: 'SQL Server geri yükleme işlemi tamamlanamadı.',
  NotClaimed: 'Restore süresi içinde Agent tarafından alınmadı. Hedef değiştirilmedi.',
  precheck_rejected: 'Ön kontrol bu geri yükleme ile eşleşmiyor veya başarılı değil. Ön kontrolü yeniden çalıştırın.',
  agent_transfer_required: 'Replica başka bir Agent üzerinde. Agentlar arası aktarım desteklenmiyor.',
  no_accessible_replica: 'Bu Agent tarafından erişilebilen Replica yok.', replica_not_restorable: 'Replica geri yüklemeye uygun değil.',
  replica_not_verified: 'Replica doğrulanmadı.', checksum_unavailable: 'Artifact checksum bilgisi yok.', backup_type_unsupported: 'Backup türü desteklenmiyor.',
  overwrite_confirmation_required: 'Üzerine yazmayı hedef Database adıyla onaylayın.',
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
  const known = ['ONLINE', 'RESTORING', 'RECOVERING', 'RECOVERY_PENDING', 'SUSPECT', 'EMERGENCY', 'OFFLINE', 'COPYING', 'OFFLINE_SECONDARY']
  return state && known.includes(state.toUpperCase()) ? state.toUpperCase() : 'Bilinmiyor'
}
export function restoreProgressPercent(value?: number | null): number | undefined {
  return value != null && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : undefined
}
