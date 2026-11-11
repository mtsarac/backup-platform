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
