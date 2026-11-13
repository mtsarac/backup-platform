import { tx } from '@/lib/i18n'
import type { StatusLevel } from '@/lib/status'
import { deletionInProgress } from './format'
import { restoreIneligibility } from './restore-eligibility'
import type { BackupArtifact } from './types'

export interface ArtifactAvailability { level: StatusLevel; label: string; reason: string | null }

/** Short status of a backup file. "Ready" only when a verified, usable copy proves it; anything uncertain is not ready. */
export function artifactAvailability(artifact: BackupArtifact): ArtifactAvailability {
  if (artifact.state === 'Deleted') return { level: 'neutral', label: tx('Silindi'), reason: restoreIneligibility(artifact) }
  if (artifact.state === 'DeleteFailed' || artifact.replicas.some(r => r.status === 'DeleteFailed')) return { level: 'urgent', label: tx('Silme başarısız'), reason: null }
  if (artifact.state === 'DeletePending' || artifact.state === 'Deleting' || deletionInProgress(artifact)) return { level: 'attention', label: tx('Siliniyor'), reason: restoreIneligibility(artifact) }
  const reason = restoreIneligibility(artifact)
  if (reason) {
    const verifying = artifact.backupType === 'Full' && artifact.replicas.some(r => r.verificationState === 'Pending')
    return verifying ? { level: 'running', label: tx('Doğrulanıyor'), reason: null } : { level: 'attention', label: tx('Uygun değil'), reason }
  }
  const degraded = artifact.replication && artifact.replication.status !== 'Healthy'
  return { level: 'safe', label: tx('Geri yüklemeye hazır'), reason: degraded ? tx('Kopya sayısı politikanın gerisinde.') : null }
}

