import { useState } from 'react'
import { useQuery, type QueryKey } from '@tanstack/react-query'
import type { DownloadStatus, PreparedDownload } from './api'
import { useDownloadFlow } from './use-download-flow'

/**
 * The controller shared by the tenant and the support artifact download: what can be downloaded (availability, polled), which replica
 * the operator picked, and the download flow. The tenant and support APIs plug in as the adapter functions; the screens stay their own.
 */
export function useArtifactDownload<Availability extends { replicas: readonly { replicaId: string; available: boolean }[] }>({ enabled, availabilityKey, fetchAvailability, refetchInterval, prepare, fetchStatus, statusKey }: {
  /** False when the caller may no longer download (permission, scope or eligibility): nothing is fetched and nothing starts. */
  enabled: boolean
  availabilityKey: QueryKey
  fetchAvailability: (signal: AbortSignal) => Promise<Availability>
  refetchInterval: number | false
  prepare: (replicaId: string | undefined) => Promise<PreparedDownload>
  fetchStatus: (sessionId: string, signal: AbortSignal) => Promise<DownloadStatus>
  statusKey: (sessionId: string | undefined) => QueryKey
}) {
  const [replicaId, setReplicaId] = useState('')
  const availability = useQuery({ queryKey: availabilityKey, queryFn: ({ signal }) => fetchAvailability(signal), enabled, refetchInterval })
  const flow = useDownloadFlow({ active: enabled, prepare: () => prepare(replicaId || undefined), fetchStatus, statusKey })
  const selected = availability.data?.replicas.find(replica => replica.replicaId === replicaId)
  return { availability, flow, replicaId, setReplicaId, selected }
}
