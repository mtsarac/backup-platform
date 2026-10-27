import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getAgentSettings, updateAgentSettings } from './api'

/** Under the `['platform']` root so logout clears it. */
export const agentSettingsKeys = { all: ['platform', 'agent-settings'] as const }

export function useAgentSettings() {
  return useQuery({ queryKey: agentSettingsKeys.all, queryFn: ({ signal }) => getAgentSettings(signal) })
}

export function useUpdateAgentSettings() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: Parameters<typeof updateAgentSettings>[0]) => updateAgentSettings(input),
    onSuccess: () => client.invalidateQueries({ queryKey: agentSettingsKeys.all }),
  })
}
