import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  clearEmailSecret, clearSmsSecret, getTransports, setEmailSecret, setSmsSecret, updateEmailTransport, updateSmsTransport,
} from './api'
import type { EmailTransportInput, SmsTransportInput } from './types'

/** Under the `['platform']` root so logout clears it. Secret VALUES are request bodies only. */
export const transportKeys = { all: ['platform', 'notification-transports'] as const }

export function useNotificationTransports() {
  return useQuery({ queryKey: transportKeys.all, queryFn: ({ signal }) => getTransports(signal) })
}

function useTransportMutation<V>(fn: (variables: V) => Promise<unknown>, secret = false) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: fn,
    gcTime: secret ? 0 : undefined,
    onSuccess: () => client.invalidateQueries({ queryKey: transportKeys.all }),
  })
}

export const useUpdateEmailTransport = () => useTransportMutation((input: EmailTransportInput) => updateEmailTransport(input))
export const useSetEmailSecret = () => useTransportMutation((value: string) => setEmailSecret(value), true)
export const useClearEmailSecret = () => useTransportMutation(() => clearEmailSecret())
export const useUpdateSmsTransport = () => useTransportMutation((input: SmsTransportInput) => updateSmsTransport(input))
export const useSetSmsSecret = () => useTransportMutation((value: string) => setSmsSecret(value), true)
export const useClearSmsSecret = () => useTransportMutation(() => clearSmsSecret())
