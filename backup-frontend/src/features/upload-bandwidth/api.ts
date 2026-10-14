import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BandwidthScope, BandwidthTarget, UploadBandwidth } from './types'

export const getUploadBandwidth = (tenantId: string, signal?: AbortSignal) => apiRequest<UploadBandwidth>(tenantPath(tenantId, 'upload-bandwidth'), { signal })

function targetPath(tenantId: string, target: BandwidthTarget) {
  if (target.scope === 'tenant') return tenantPath(tenantId, 'upload-bandwidth/tenant')
  return tenantPath(tenantId, `upload-bandwidth/${target.scope}s/${encodeURIComponent(target.id)}`)
}

/** `bytesPerSecond: null` removes the limit (Unlimited). */
export const setUploadBandwidth = (tenantId: string, target: BandwidthTarget, bytesPerSecond: number | null) =>
  apiRequest<{ scope: BandwidthScope; scopeId: string; bytesPerSecond: number | null }>(targetPath(tenantId, target), { method: 'PUT', json: { bytesPerSecond } })
