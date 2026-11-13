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

/**
 * What a listing can say about downloading without probing storage or the agent: a definite "no", or "needs a check".
 * Whether the stored object is reachable right now is only known to the detail page's availability request, so a listing never says "ready".
 */
export function downloadAvailability(artifact: BackupArtifact, canDownload: boolean): ArtifactAvailability {
  if (artifact.state !== 'Available' || !artifact.replicas.some(r => r.status === 'Available' && r.verificationState === 'Verified')) return { level: 'neutral', label: tx('Uygun değil'), reason: null }
  // PlatformAdmin downloads only through a BackupShare.
  return { level: 'neutral', label: canDownload ? tx('Kontrol gerekli') : tx('Paylaşım gerekli'), reason: null }
}
