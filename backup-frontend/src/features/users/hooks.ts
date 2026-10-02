import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { User } from '@/lib/types'
import { getUsers, updateUserStatus } from './api'

export const userQueryKeys = {
  all: ['users'],
} as const

export function useUsers() {
  return useQuery({
    queryKey: userQueryKeys.all,
    queryFn: getUsers,
  })
}

export function useUpdateUserStatus() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ id, status }: { id: string; status: User['status'] }) =>
      updateUserStatus(id, status),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: userQueryKeys.all })
    },
  })
}
