import { useQuery } from '@tanstack/react-query'
import { getStorageProviders } from './api'

export const storageQueryKeys = {
  all: ['storageProviders'],
} as const

export function useStorageProviders() {
  return useQuery({
    queryKey: storageQueryKeys.all,
    queryFn: getStorageProviders,
  })
}
