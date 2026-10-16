import { tx } from '@/lib/i18n'
import type { BackupArtifact } from '@/features/artifacts/types'

/** One explanation for the header action and the restore section; eligibility stays unchanged. */
export function restoreIneligibility(artifact: BackupArtifact): string | null {
  if (artifact.state === 'Deleted') return tx('Bu backup dosyasının tüm kopyaları silindiği için geri yüklenemez.')
  if (artifact.state === 'DeletePending') return tx('Bu backup silme bekleme süresindedir (DeletePending). Yeni geri yükleme başlatılamaz.')
  if (artifact.backupType !== 'Full') return tx("Yalnızca tam (Full) backup türü geri yüklenebilir (Mevcut tür: {0}).", { "0": artifact.backupType })
  if (artifact.replicas.length === 0) return tx('Geri yüklenebilecek replika kaydı bulunamadı (Missing replica).')
  if (!artifact.replicas.some((r) => r.verificationState === 'Verified')) return tx('Replika bütünlük doğrulaması başarısız veya tamamlanmamış (Verification failed).')
  return null
}
