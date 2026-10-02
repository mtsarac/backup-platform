import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { createBackupJob, getBackupJob, getBackupJobs } from './api'

export const backupJobQueryKeys = {
  all: ['backupJobs'],
  detail: (id: string) => ['backupJobs', id],
} as const

export function useBackupJobs() {
  return useQuery({
    queryKey: backupJobQueryKeys.all,
    queryFn: getBackupJobs,
  })
}

export function useBackupJob(id: string) {
  return useQuery({
    queryKey: backupJobQueryKeys.detail(id),
    queryFn: () => getBackupJob(id),
    enabled: id.length > 0,
  })
}

export function useCreateBackupJob() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: createBackupJob,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: backupJobQueryKeys.all })
    },
  })
}
