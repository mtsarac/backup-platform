import { useQuery } from '@tanstack/react-query'
import { getClient, getClients } from './api'

export const clientQueryKeys = {
  all: ['clients'],
  detail: (id: string) => ['clients', id],
} as const

export function useClients() {
  return useQuery({
    queryKey: clientQueryKeys.all,
    queryFn: getClients,
  })
}

export function useClient(id: string) {
  return useQuery({
    queryKey: clientQueryKeys.detail(id),
    queryFn: () => getClient(id),
  })
}
