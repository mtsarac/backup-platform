import { ownershipLabel } from '@/features/storage/labels'
import type { BackupConfiguration, BackupDestination } from '@/features/backup-jobs/types'
import type { ConnectionView, ProviderDescriptor } from './types'

export function connectionUnavailableReason(connection: ConnectionView | undefined, provider: ProviderDescriptor | undefined): string | null {
  if (!connection) return 'Bağlantı bulunamadı'
  if (connection.administrativelyDisabled) return 'Platform kilidi'
  if (!connection.effectiveEnabled) return 'Devre dışı'
  if (!provider) return 'Desteklenmeyen hedef'
  if (connection.availability !== 'Available' || provider.availability !== 'Available') return 'Sağlayıcı kullanılamıyor'
  if (!provider.executionLocation || !provider.topology || !provider.supportedOwnerships.includes(connection.ownership)) return 'Desteklenmeyen hedef'
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

/** Shared catalog labels; selection rules differ for backup execution and quota accounting. */
export function connectionOptions(connections: readonly ConnectionView[], providers: readonly ProviderDescriptor[], includeTenant = false) {
  return connections.map((connection) => {
    const unavailable = connectionUnavailableReason(connection, providers.find((provider) => provider.providerType === connection.providerType))
    return {
      id: connection.id,
      unavailable,
      label: [includeTenant ? connection.tenantName ?? 'Tenant adı kullanılamıyor' : null, connection.name, connection.providerDisplayName, ownershipLabel(connection.ownership), unavailable ?? 'Kullanılabilir'].filter(Boolean).join(' · '),
    }
  })
}
