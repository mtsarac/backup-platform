import { apiRequest } from '@/lib/api/client'

export interface AgentSettings {
  serverUrl: string | null
  enrollmentTokenLifetimeMinutes: number
  limits: { minMinutes: number; maxMinutes: number }
  updatedAt: string | null
}

export interface AgentSettingsInput { serverUrl: string | null; enrollmentTokenLifetimeMinutes: number }

const path = 'platform/agent-settings'

export function getAgentSettings(signal?: AbortSignal) {
  return apiRequest<AgentSettings>(path, { signal })
}

export function updateAgentSettings(input: AgentSettingsInput) {
  return apiRequest<AgentSettings>(path, { method: 'PUT', json: input })
}
