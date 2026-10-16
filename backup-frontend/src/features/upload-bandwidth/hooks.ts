import { tx } from '@/lib/i18n'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getUploadBandwidth, setUploadBandwidth } from './api'
import type { BandwidthTarget } from './types'

export const uploadBandwidthQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'upload-bandwidth'] as const,
}
export function useUploadBandwidth() {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: uploadBandwidthQueryKeys.all(tenantId), queryFn: ({ signal }) => getUploadBandwidth(tenantId, signal) })
}
export function useSetUploadBandwidth() {
  const { tenantId, canAdmin } = useTenant()
  const client = useQueryClient()
  const invalidate = () => client.invalidateQueries({ queryKey: uploadBandwidthQueryKeys.all(tenantId) })
  return useMutation({ mutationFn: ({ target, bytesPerSecond }: { target: BandwidthTarget; bytesPerSecond: number | null }) => {
    if (!canAdmin) throw new Error(tx('Bu işlem için yetkiniz yok.'))
    return setUploadBandwidth(tenantId, target, bytesPerSecond)
  }, onSuccess: invalidate, onError: invalidate })
}
