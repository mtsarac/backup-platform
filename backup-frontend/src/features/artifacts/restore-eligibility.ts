import type { BackupArtifact } from '@/features/artifacts/types'

/** One explanation for the header action and the restore section; eligibility stays unchanged. */
export function restoreIneligibility(artifact: BackupArtifact): string | null {
  if (artifact.state === 'Deleted') return 'Bu backup dosyasının tüm kopyaları silindiği için geri yüklenemez.'
  if (artifact.state === 'DeletePending') return 'Bu backup silme bekleme süresindedir (DeletePending). Yeni geri yükleme başlatılamaz.'
  if (artifact.backupType !== 'Full') return `Yalnızca tam (Full) backup türü geri yüklenebilir (Mevcut tür: ${artifact.backupType}).`
  if (artifact.replicas.length === 0) return 'Geri yüklenebilecek replika kaydı bulunamadı (Missing replica).'
  if (!artifact.replicas.some((r) => r.verificationState === 'Verified')) return 'Replika bütünlük doğrulaması başarısız veya tamamlanmamış (Verification failed).'
  return null
}
