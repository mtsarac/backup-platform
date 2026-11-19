import { tx } from '@/lib/i18n'
import { ownershipLabel } from '@/features/storage/labels'
import type { BackupConfiguration, BackupDestination } from '@/features/backup-jobs/types'
import { connectionDisplayName, storageProviderLabel } from './labels'
import type { ConnectionView, ProviderDescriptor } from './types'

export function connectionUnavailableReason(connection: ConnectionView | undefined, provider: ProviderDescriptor | undefined): string | null {
  if (!connection) return tx('Bağlantı bulunamadı')
  if (connection.administrativelyDisabled) return tx('Platform kilidi')
  if (!connection.effectiveEnabled) return tx('Devre dışı')
  if (!provider) return tx('Desteklenmeyen hedef')
  if (connection.availability !== 'Available' || provider.availability !== 'Available') return tx('Sağlayıcı kullanılamıyor')
  if (!provider.executionLocation || !provider.topology || !provider.supportedOwnerships.includes(connection.ownership)) return tx('Desteklenmeyen hedef')
  return null
}

export function primaryDestination(configuration: BackupConfiguration): BackupDestination {
  return configuration.destinations.find(destination => destination.isPrimary) ?? configuration.destinations[0]
}

export function resolveDestinationConnection(destination: BackupDestination, connections: readonly ConnectionView[]) {
  return connections.find(connection => destination.storageConnectionId
    ? connection.id === destination.storageConnectionId
    : connection.providerType === destination.storageProvider && connection.isDefault)
}

/** What a job destination is called to the user; no chosen connection reads as the local disk. */
export function destinationName(destination: BackupDestination, connections: readonly ConnectionView[]) {
  const connection = resolveDestinationConnection(destination, connections)
  if (connection) return connectionDisplayName(connection)
  if (destination.storageConnectionId) return tx('Bağlantı bulunamadı')
  return destination.storageProvider === 'local' ? tx('Yerel disk') : storageProviderLabel(destination.storageProvider)
}

/** Shared catalog labels; selection rules differ for backup execution and quota accounting. */
export function connectionOptions(connections: readonly ConnectionView[], providers: readonly ProviderDescriptor[], includeTenant = false) {
  return connections.map((connection) => {
    const unavailable = connectionUnavailableReason(connection, providers.find((provider) => provider.providerType === connection.providerType))
    return {
      id: connection.id,
      unavailable,
      label: [includeTenant ? connection.tenantName ?? tx('Tenant adı kullanılamıyor') : null, includeTenant ? connection.name : connectionDisplayName(connection), tx(connection.providerDisplayName), includeTenant || connection.ownership !== 'TenantManaged' ? ownershipLabel(connection.ownership) : null, unavailable ?? tx('Kullanılabilir')].filter(Boolean).join(' · '),
    }
  })
}
