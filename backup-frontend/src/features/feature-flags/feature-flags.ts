import { apiRequest } from '@/lib/api/client'

/** Keys of `GET /api/features`; a new feature is one more union member. */
export type FeatureKey = 'sms' | 'supportSessions' | 'smb' | 'sizeAnomaly' | 'windowsVss' | 'linuxSnapshots' | 'externalSecrets' | 'replicaRepair'

let flags: Partial<Record<FeatureKey, boolean>> = {}

/** Read once before the first render. If the backend cannot answer, every flagged feature stays hidden. */
export async function loadFeatureFlags() {
  flags = await apiRequest<typeof flags>('features').catch(() => ({}))
}

export const isFeatureEnabled = (key: FeatureKey) => flags[key] === true

export const setFeatureFlags = (next: typeof flags) => { flags = next }
