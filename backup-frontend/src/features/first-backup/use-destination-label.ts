import { tx } from '@/lib/i18n'
import { useTenantConnections } from '@/features/storage-connections/hooks'
import { resolveDestinationConnection } from '@/features/storage-connections/job-connection-picker-utils'
import type { FirstBackupSettings } from './first-backup-model'

/** The connection name, or "Yerel disk" for the default local destination. */
export function useDestinationLabel(settings: FirstBackupSettings) {
  const connections = useTenantConnections()
  const destination = settings.destinations[0]
  if (!destination.storageConnectionId) return tx('Yerel disk')
  return resolveDestinationConnection(destination, connections.data?.items ?? [])?.name ?? tx('Bağlantı bulunamadı')
}
