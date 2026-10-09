import type { BackupArtifact } from './types'

const dateFormat = new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium', timeStyle: 'short' })

export function artifactDate(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : dateFormat.format(date)
}

export function artifactSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length)
  return `${(bytes / 1024 ** exponent).toLocaleString('tr-TR', { maximumFractionDigits: 2 })} ${units[exponent - 1]}`
}

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
  if (reason === 'Manual') return 'Yönetici tarafından istendi'
  if (reason === 'RetentionExpired') return 'Saklama süresi doldu'
  return '—'
}
