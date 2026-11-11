import { apiRequest } from '@/lib/api/client'
import type { BackupShareView, CreateShareRequest } from './types'

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
