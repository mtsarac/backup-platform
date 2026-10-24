import { tx, useLanguage } from '@/lib/i18n'
import type { UseQueryResult } from '@tanstack/react-query'
import { Section } from '@/components/ui/section'
import type { Agent } from '@/features/agents/types'
import { FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import { errorMessage } from '@/lib/api/failure-messages'

interface Props {
  name: string; onName: (name: string) => void
  agentId: string; onAgentId: (agentId: string) => void
  enabled: boolean; onEnabled: (enabled: boolean) => void
  agents: Pick<UseQueryResult<Agent[]>, 'data' | 'isPending' | 'error'>
  errors: FieldErrors
}

/** Question group: which job is this and which Agent runs it. */
export function GeneralStep({ name, onName, agentId, onAgentId, enabled, onEnabled, agents, errors }: Props) {
  useLanguage()
  const { text, check, select } = fieldKit(errors)
  return <Section title={tx("İş ve Agent")} description={tx("İşin adı ve yedeği alacak Agent.")}>
    <FormGrid>
      {text('name', tx('İş adı'), name, onName)}
      <div className="min-w-0 space-y-1">
        {select('agentId', tx('Agent'), agentId, [['', tx('Agent seçin')], ...(agents.data ?? []).map(item => [item.agentId, `${item.hostName ?? tx('Adsız Agent')} · ${tx(item.operationalStatus)}`] as const)], onAgentId)}
        {agents.isPending && <p role="status" className="text-sm text-text-muted">{tx("Agent listesi yükleniyor…")}</p>}{agents.error && <p role="alert" className="text-sm text-error">{errorMessage(agents.error)}</p>}
      </div>
      {check(tx('İş etkin'), enabled, onEnabled)}
    </FormGrid>
  </Section>
}
