import { apiRequest, tenantPath } from '@/lib/api/client'
import { query } from '@/features/platform/api'
import type { PageParams, PagedResult } from '@/features/platform/types'
import type { SupportAccessActivity, SupportAccessSession } from './types'

const sessionsPath = (tenantId: string) => tenantPath(tenantId, 'support-access/sessions')

export function getSupportAccessSessions(tenantId: string, params: PageParams, signal?: AbortSignal) {
  return apiRequest<PagedResult<SupportAccessSession>>(`${sessionsPath(tenantId)}${query(params)}`, { signal })
}

export function getSupportAccessSession(tenantId: string, sessionId: string, signal?: AbortSignal) {
  return apiRequest<SupportAccessSession>(`${sessionsPath(tenantId)}/${encodeURIComponent(sessionId)}`, { signal })
}

export function getSupportAccessActivity(tenantId: string, sessionId: string, params: PageParams, signal?: AbortSignal) {
  return apiRequest<PagedResult<SupportAccessActivity>>(`${sessionsPath(tenantId)}/${encodeURIComponent(sessionId)}/activity${query(params)}`, { signal })
}
