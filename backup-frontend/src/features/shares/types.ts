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

export function canManageShare(share: BackupShareView, role?: string, canAdmin?: boolean): boolean {
  if (share.status !== 'Active') return false
  if (role === 'PlatformAdmin') return true
  if (role === 'UserAdmin' && canAdmin) {
    return share.createdByRole !== 'PlatformAdmin'
  }
  return false
}
