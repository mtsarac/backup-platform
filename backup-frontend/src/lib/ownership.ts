import type { Backup, BackupJob, Client, StorageProvider, User } from './types'

const knownOwners: Record<string, string> = { '1': 'admin', 'user-1': 'user', 'user-2': 'user-2' }

export function isAdministrator(user: User | null | undefined): boolean {
  return user?.role === 'administrator'
}

export function canManageStorage(user: User | null | undefined): boolean {
  return isAdministrator(user)
}

export function displayOwner(ownerUserId: string): string {
  return knownOwners[ownerUserId] ?? ownerUserId
}

export function visibleClients(
  clients: readonly Client[],
  user: User | null | undefined
): readonly Client[] {
  if (isAdministrator(user)) return clients
  if (!user) return []
  return clients.filter((client) => client.ownerUserId === user.id)
}

export function visibleBackupJobs(
  jobs: readonly BackupJob[],
  clients: readonly Client[],
  user: User | null | undefined
): readonly BackupJob[] {
  if (isAdministrator(user)) return jobs
  return jobs.filter((job) => clients.some((client) => client.id === job.clientId))
}

export function visibleBackups(
  backups: readonly Backup[],
  clients: readonly Client[],
  user: User | null | undefined
): readonly Backup[] {
  if (isAdministrator(user)) return backups
  return backups.filter((backup) => clients.some((client) => client.id === backup.clientId))
}

export function visibleStorages(
  providers: readonly StorageProvider[],
  user: User | null | undefined
): readonly StorageProvider[] {
  if (isAdministrator(user)) return providers
  if (!user) return []
  return providers.filter((provider) => provider.ownerUserId === user.id)
}
