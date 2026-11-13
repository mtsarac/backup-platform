import { apiRequest } from '@/lib/api/client'
import type {
  AuditRetentionSettings,
  BackupShareView,
  CreatePlatformShareRequest,
  CreateShareRequest,
  DownloadAttemptFilters,
  DownloadAttemptPage,
  ExtendShareRequest,
  RevokeShareRequest,
} from './types'

export async function listShares(tenantId: string, artifactId?: string, signal?: AbortSignal): Promise<BackupShareView[]> {
  const query = artifactId ? `?artifactId=${encodeURIComponent(artifactId)}` : ''
  return apiRequest<BackupShareView[]>(`tenants/${encodeURIComponent(tenantId)}/backup-shares${query}`, { signal })
}

export async function getShare(tenantId: string, shareId: string, signal?: AbortSignal): Promise<BackupShareView> {
  return apiRequest<BackupShareView>(`tenants/${encodeURIComponent(tenantId)}/backup-shares/${encodeURIComponent(shareId)}`, { signal })
}

export async function createShare(tenantId: string, artifactId: string, request: CreateShareRequest): Promise<BackupShareView> {
  return apiRequest<BackupShareView>(`tenants/${encodeURIComponent(tenantId)}/backup-artifacts/${encodeURIComponent(artifactId)}/shares`, {
    method: 'POST',
    json: request,
  })
}

export async function listPlatformShares(
  params?: { tenantId?: string; status?: string },
  signal?: AbortSignal
): Promise<BackupShareView[]> {
  const search = new URLSearchParams()
  if (params?.tenantId) search.set('tenantId', params.tenantId)
  if (params?.status) search.set('status', params.status)
  const query = search.toString() ? `?${search.toString()}` : ''
  return apiRequest<BackupShareView[]>(`platform/backup-shares${query}`, { signal })
}

export async function getPlatformShare(shareId: string, signal?: AbortSignal): Promise<BackupShareView> {
  return apiRequest<BackupShareView>(`platform/backup-shares/${encodeURIComponent(shareId)}`, { signal })
}

export async function createPlatformShare(request: CreatePlatformShareRequest): Promise<BackupShareView> {
  return apiRequest<BackupShareView>('platform/backup-shares', {
    method: 'POST',
    json: request,
  })
}
export async function extendShare(
  tenantId: string,
  shareId: string,
  request: ExtendShareRequest
): Promise<BackupShareView> {
  return apiRequest<BackupShareView>(
    `tenants/${encodeURIComponent(tenantId)}/backup-shares/${encodeURIComponent(shareId)}/extend`,
    {
      method: 'POST',
      json: request,
    }
  )
}

export async function revokeShare(
  tenantId: string,
  shareId: string,
  request?: RevokeShareRequest
): Promise<BackupShareView> {
  return apiRequest<BackupShareView>(
    `tenants/${encodeURIComponent(tenantId)}/backup-shares/${encodeURIComponent(shareId)}/revoke`,
    {
      method: 'POST',
      json: request ?? {},
    }
  )
}

function buildAttemptQuery(filters?: DownloadAttemptFilters, cursor?: string): string {
  const search = new URLSearchParams()
  if (filters?.shareId) search.set('shareId', filters.shareId)
  if (filters?.tenantId) search.set('tenantId', filters.tenantId)
  if (filters?.artifactId) search.set('artifactId', filters.artifactId)
  if (filters?.userId) search.set('userId', filters.userId)
  if (filters?.result) search.set('result', filters.result)
  if (cursor) search.set('cursor', cursor)
  const qs = search.toString()
  return qs ? `?${qs}` : ''
}

export function listTenantDownloadAttempts(tenantId: string, filters?: DownloadAttemptFilters, cursor?: string, signal?: AbortSignal) {
  return apiRequest<DownloadAttemptPage>(`tenants/${encodeURIComponent(tenantId)}/backup-shares/download-attempts${buildAttemptQuery(filters, cursor)}`, { signal })
}

export function listPlatformDownloadAttempts(filters?: DownloadAttemptFilters, cursor?: string, signal?: AbortSignal) {
  return apiRequest<DownloadAttemptPage>(`platform/download-attempts${buildAttemptQuery(filters, cursor)}`, { signal })
}

export function getAuditRetention(signal?: AbortSignal): Promise<AuditRetentionSettings> {
  return apiRequest<AuditRetentionSettings>('platform/settings/audit-retention', { signal })
}

export async function updateAuditRetention(retentionDays: number): Promise<AuditRetentionSettings> {
  return apiRequest<AuditRetentionSettings>('platform/settings/audit-retention', {
    method: 'PUT',
    json: { retentionDays },
  })
}
