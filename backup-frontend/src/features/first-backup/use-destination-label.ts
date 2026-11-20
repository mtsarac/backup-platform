import { useTenantConnections } from '@/features/storage-connections/hooks'
import { destinationName } from '@/features/storage-connections/job-connection-picker-utils'
import type { FirstBackupSettings } from './first-backup-model'

/** The connection's plain name; the default local destination reads "Yerel disk". */
export function useDestinationLabel(settings: FirstBackupSettings) {
  const connections = useTenantConnections()
  return destinationName(settings.destinations[0], connections.data?.items ?? [])
}
