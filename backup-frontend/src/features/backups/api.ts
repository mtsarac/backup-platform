import { mockBackups } from '@/lib/api/mock-data'
import type { Backup } from '@/lib/types'

export function getBackups(): Promise<readonly Backup[]> {
  return Promise.resolve(mockBackups)
}

export function getBackup(id: string): Promise<Backup | undefined> {
  return Promise.resolve(mockBackups.find((backup) => backup.id === id))
}
