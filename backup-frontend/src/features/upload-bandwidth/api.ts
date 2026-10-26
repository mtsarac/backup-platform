import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BandwidthProfile, BandwidthScope, BandwidthTarget, TransferScheduleWindowDto, UploadBandwidth } from './types'

export const getUploadBandwidth = (tenantId: string, signal?: AbortSignal) => apiRequest<UploadBandwidth>(tenantPath(tenantId, 'upload-bandwidth'), { signal })

function targetPath(tenantId: string, target: BandwidthTarget) {
  if (target.scope === 'tenant') return tenantPath(tenantId, 'upload-bandwidth/tenant')
  return tenantPath(tenantId, `upload-bandwidth/${target.scope}s/${encodeURIComponent(target.id)}`)
}

/** `bytesPerSecond: null` removes the limit (Unlimited). */
export const setUploadBandwidth = (tenantId: string, target: BandwidthTarget, bytesPerSecond: number | null) =>
  apiRequest<{ scope: BandwidthScope; scopeId: string; bytesPerSecond: number | null }>(targetPath(tenantId, target), { method: 'PUT', json: { bytesPerSecond } })

export const getBandwidthProfiles = (tenantId: string, signal?: AbortSignal) =>
  apiRequest<BandwidthProfile[]>(tenantPath(tenantId, 'bandwidth-profiles'), { signal })

export const saveBandwidthProfile = (tenantId: string, profile: { id?: string; name: string; timeZoneId: string; windows: TransferScheduleWindowDto[] }) =>
  profile.id
    ? apiRequest<BandwidthProfile>(tenantPath(tenantId, `bandwidth-profiles/${encodeURIComponent(profile.id)}`), { method: 'PUT', json: { name: profile.name, timeZoneId: profile.timeZoneId, windows: profile.windows } })
    : apiRequest<BandwidthProfile>(tenantPath(tenantId, 'bandwidth-profiles'), { method: 'POST', json: { name: profile.name, timeZoneId: profile.timeZoneId, windows: profile.windows } })

export const deleteBandwidthProfile = (tenantId: string, profileId: string) =>
  apiRequest<void>(tenantPath(tenantId, `bandwidth-profiles/${encodeURIComponent(profileId)}`), { method: 'DELETE' })

export const assignBandwidthProfile = (tenantId: string, target: BandwidthTarget, profileId: string | null) =>
  apiRequest<{ scope: BandwidthScope; scopeId: string; profileId: string | null }>(`${targetPath(tenantId, target)}/profile`, { method: 'PUT', json: { profileId } })
