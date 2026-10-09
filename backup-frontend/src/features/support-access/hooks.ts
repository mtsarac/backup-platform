import { useQuery } from '@tanstack/react-query'
import type { PageParams } from '@/features/platform/types'
import { getSupportAccessActivity, getSupportAccessSession, getSupportAccessSessions } from './api'

export const supportAccessKeys = {
  sessions: (tenantId: string, params: PageParams) => ['tenant', tenantId, 'support-access', 'sessions', params] as const,
  session: (tenantId: string, sessionId: string) => ['tenant', tenantId, 'support-access', 'session', sessionId] as const,
  activity: (tenantId: string, sessionId: string, params: PageParams) => ['tenant', tenantId, 'support-access', 'session', sessionId, 'activity', params] as const,
}

export function useSupportAccessSessions(tenantId: string, params: PageParams) {
  return useQuery({ queryKey: supportAccessKeys.sessions(tenantId, params), queryFn: ({ signal }) => getSupportAccessSessions(tenantId, params, signal) })
}

export function useSupportAccessSession(tenantId: string, sessionId: string) {
  return useQuery({ queryKey: supportAccessKeys.session(tenantId, sessionId), queryFn: ({ signal }) => getSupportAccessSession(tenantId, sessionId, signal) })
}

export function useSupportAccessActivity(tenantId: string, sessionId: string, params: PageParams) {
  return useQuery({ queryKey: supportAccessKeys.activity(tenantId, sessionId, params), queryFn: ({ signal }) => getSupportAccessActivity(tenantId, sessionId, params, signal) })
}
