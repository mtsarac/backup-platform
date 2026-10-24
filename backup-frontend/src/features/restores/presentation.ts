import { tx } from '@/lib/i18n'
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
