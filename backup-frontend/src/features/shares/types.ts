export type ShareStatus = 'Active' | 'Expired' | 'Revoked'

export interface BackupShareView {
  id: string
  tenantId: string
  artifactId: string
  createdByUserId: string
  createdByRole: string
  reason: string | null
  createdAtUtc: string
  expiresAtUtc: string
  revokedAtUtc: string | null
  revokedByUserId: string | null
  status: ShareStatus
  artifactName?: string | null
  createdByDisplayName?: string | null
  tenantName?: string | null
}

export interface CreatePlatformShareRequest {
  tenantId: string
  artifactId: string
  durationHours?: number
  reason?: string
}

export interface CreateShareRequest {
  durationHours?: number
  reason?: string
}

export interface ExtendShareRequest {
  durationHours?: number
  additionalHours?: number
  expiresAtUtc?: string
}

export interface RevokeShareRequest {
  reason?: string
}

export type DownloadAttemptResult = 'Completed' | 'Failed' | 'Cancelled' | 'Refused' | 'Streaming' | 'Ready' | 'Preparing'

export interface DownloadAttemptView {
  id: string
  shareId: string | null
  tenantId: string
  tenantName?: string | null
  artifactId: string
  artifactName?: string | null
  initiatingUserId: string
  initiatingUserDisplayName?: string | null
  startedAtUtc: string
  endedAtUtc: string | null
  result: DownloadAttemptResult
  bytesTransferred: number
  failureReason: string | null
  decrypted: boolean
}

/** One page of the history, newest first; `nextCursor` is null on the last page. */
export interface DownloadAttemptPage {
  items: DownloadAttemptView[]
  nextCursor: string | null
}

export interface DownloadAttemptFilters {
  shareId?: string
  tenantId?: string
  artifactId?: string
  userId?: string
  result?: string
}

export interface AuditRetentionSettings {
  retentionDays: number
  defaultRetentionDays: number
  minRetentionDays: number
  maxRetentionDays: number
  updatedAtUtc?: string | null
  updatedByUserId?: string | null
}

export function canManageShare(share: BackupShareView, role?: string): boolean {
  if (share.status !== 'Active') return false
  if (role === 'PlatformAdmin') return true
  if (role === 'UserAdmin') {
    return share.createdByRole !== 'PlatformAdmin'
  }
  return false
}

export const MAX_SHARE_HOURS = 72
export const SHARE_DURATION_HOURS = [1, 24, 72] as const

/** The fixed durations that still fit under the 72-hour total, counted from creation; empty once the share has no room left. */
export function extensionHours(share: Pick<BackupShareView, 'createdAtUtc' | 'expiresAtUtc'>): number[] {
  const room = (new Date(share.createdAtUtc).getTime() + MAX_SHARE_HOURS * 3_600_000 - new Date(share.expiresAtUtc).getTime()) / 3_600_000
  return SHARE_DURATION_HOURS.filter((hours) => hours <= room)
}

export function canExtendShare(share: BackupShareView, role?: string): boolean {
  return canManageShare(share, role) && extensionHours(share).length > 0
}
