import { tx } from '@/lib/i18n'
import type { BackupArtifact } from '@/features/artifacts/types'

/** One plain explanation of why a backup file cannot be restored, or null when it can; shown in the answers and gates the restore action. */
export function restoreIneligibility(artifact: BackupArtifact): string | null {
  if (artifact.state === 'Deleted') return tx('Bu yedek dosyasının tüm kopyaları silindiği için geri yüklenemez.')
  if (artifact.state === 'DeletePending') return tx('Bu yedek dosyası silme bekleme süresinde olduğu için yeni geri yükleme başlatılamaz.')
  if (artifact.backupType !== 'Full') return tx("Yalnızca tam yedekler geri yüklenebilir (bu dosyanın türü: {0}).", { "0": artifact.backupType })
  if (artifact.replicas.length === 0) return tx('Geri yüklenebilecek bir kopya bulunamadı.')
  if (!artifact.replicas.some((r) => r.verificationState === 'Verified')) return tx('Hiçbir kopyanın bütünlüğü doğrulanmadığı için geri yüklenemez.')
  return null
}
