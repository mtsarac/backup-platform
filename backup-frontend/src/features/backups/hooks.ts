import { useQuery } from '@tanstack/react-query'
import { getBackup, getBackups } from './api'

export const backupQueryKeys = {
  all: ['backups'],
  detail: (id: string) => ['backups', id],
} as const

export function useBackups() {
  return useQuery({
    queryKey: backupQueryKeys.all,
    queryFn: getBackups,
  })
}

export function useBackup(id: string) {
  return useQuery({
    queryKey: backupQueryKeys.detail(id),
    queryFn: () => getBackup(id),
    enabled: id.length > 0,
  })
}
