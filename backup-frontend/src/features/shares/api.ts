import { apiRequest } from '@/lib/api/client'
import type { BackupShareView, CreatePlatformShareRequest, CreateShareRequest } from './types'

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
