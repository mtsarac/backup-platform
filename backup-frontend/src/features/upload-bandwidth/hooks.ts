import { tx } from '@/lib/i18n'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { assignBandwidthProfile, deleteBandwidthProfile, getBandwidthProfiles, getUploadBandwidth, saveBandwidthProfile, setUploadBandwidth } from './api'
import type { BandwidthTarget, TransferScheduleWindowDto } from './types'

export const uploadBandwidthQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'upload-bandwidth'] as const,
  profiles: (tenantId: string) => ['tenant', tenantId, 'bandwidth-profiles'] as const,
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

export function useBandwidthProfiles() {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: uploadBandwidthQueryKeys.profiles(tenantId), queryFn: ({ signal }) => getBandwidthProfiles(tenantId, signal) })
}

export function useSaveBandwidthProfile() {
  const { tenantId, canAdmin } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: (profile: { id?: string; name: string; timeZoneId: string; windows: TransferScheduleWindowDto[] }) => {
      if (!canAdmin) throw new Error(tx('Bu işlem için yetkiniz yok.'))
      return saveBandwidthProfile(tenantId, profile)
    },
    onSuccess: () => {
      client.invalidateQueries({ queryKey: uploadBandwidthQueryKeys.profiles(tenantId) })
      client.invalidateQueries({ queryKey: uploadBandwidthQueryKeys.all(tenantId) })
    }
  })
}

export function useDeleteBandwidthProfile() {
  const { tenantId, canAdmin } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: (profileId: string) => {
      if (!canAdmin) throw new Error(tx('Bu işlem için yetkiniz yok.'))
      return deleteBandwidthProfile(tenantId, profileId)
    },
    onSuccess: () => {
      client.invalidateQueries({ queryKey: uploadBandwidthQueryKeys.profiles(tenantId) })
      client.invalidateQueries({ queryKey: uploadBandwidthQueryKeys.all(tenantId) })
    }
  })
}

export function useAssignBandwidthProfile() {
  const { tenantId, canAdmin } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ target, profileId }: { target: BandwidthTarget; profileId: string | null }) => {
      if (!canAdmin) throw new Error(tx('Bu işlem için yetkiniz yok.'))
      return assignBandwidthProfile(tenantId, target, profileId)
    },
    onSuccess: () => client.invalidateQueries({ queryKey: uploadBandwidthQueryKeys.all(tenantId) })
  })
}
