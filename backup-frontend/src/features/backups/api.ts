import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BackupRun, BackupRunFilters, BackupRunPage } from './types'

export function backupRunSearch(filters: BackupRunFilters): string {
  const params = new URLSearchParams()
  for (const key of ['status', 'jobId', 'agentId', 'from', 'to', 'trigger'] as const) {
    const value = filters[key]
    if (value) params.set(key, value)
  }
  params.set('page', String(filters.page ?? 1))
  params.set('pageSize', String(filters.pageSize ?? 25))
  return params.toString()
}

export function getBackups(tenantId: string, filters: BackupRunFilters = {}, signal?: AbortSignal): Promise<BackupRunPage> {
  return apiRequest(`${tenantPath(tenantId, 'backup-runs')}?${backupRunSearch(filters)}`, { signal })
}

export function getBackup(tenantId: string, id: string, signal?: AbortSignal): Promise<BackupRun> {
  return apiRequest(tenantPath(tenantId, `backup-runs/${encodeURIComponent(id)}`), { signal })
}

export function retryBackup(tenantId: string, id: string): Promise<BackupRun> {
  return apiRequest(tenantPath(tenantId, `backup-runs/${encodeURIComponent(id)}/retry`), { method: 'POST' })
}

export function cancelBackup(tenantId: string, id: string): Promise<BackupRun> {
  return apiRequest(tenantPath(tenantId, `backup-runs/${encodeURIComponent(id)}/cancel`), { method: 'POST' })
}
