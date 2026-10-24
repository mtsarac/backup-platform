import { tx } from '@/lib/i18n'
import type { BackupArtifact } from './types'

export function artifactState(artifact: { state: string; replicas: { status: string }[] }): string {
  if (artifact.state === 'Deleted') return 'Deleted'
  if (artifact.replicas.some((replica) => replica.status === 'DeleteFailed')) return 'DeleteFailed'
  return artifact.state
}

export function deletionInProgress(artifact: BackupArtifact): boolean {
  return artifact.state !== 'Deleted' && artifact.replicas.some((replica) =>
    ['DeletePending', 'Deleting', 'DeleteFailed'].includes(replica.status))
}

export function canCancelDeletion(artifact: BackupArtifact): boolean {
  if (artifact.cancelDeleteAllowed === false) return false
  return artifact.state !== 'Deleted'
    && artifact.replicas.some((replica) => replica.status === 'DeletePending')
    && artifact.replicas.every((replica) => replica.deleteAttempts === 0
      && !['Deleting', 'DeleteFailed', 'Deleted'].includes(replica.status))
}

export function deletionReason(reason: string | null): string {
  if (reason === 'Manual') return tx('Yönetici tarafından istendi')
  if (reason === 'RetentionExpired') return tx('Saklama süresi doldu')
  return '—'
}
