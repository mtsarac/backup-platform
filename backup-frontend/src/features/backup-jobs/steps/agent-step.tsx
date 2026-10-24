import { tx, useLanguage } from '@/lib/i18n'
import type { UseQueryResult } from '@tanstack/react-query'
import type { Agent } from '@/features/agents/types'
import { FormGrid, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import { errorMessage } from '@/lib/api/failure-messages'

interface Props {
  agentId: string; onAgentId: (agentId: string) => void
  agents: Pick<UseQueryResult<Agent[]>, 'data' | 'isPending' | 'error'>
  errors: FieldErrors
}

/** Question: which backup computer takes the backup. */
export function AgentStep({ agentId, onAgentId, agents, errors }: Props) {
  useLanguage()
  const { select } = fieldKit(errors)
  return <Question title={tx("Yedeği hangi bilgisayar alacak?")} why={tx("Yedek, veritabanına ulaşabilen bir yedekleme bilgisayarında çalışan program tarafından alınır.")}>
    <FormGrid>
      <div className="min-w-0 space-y-1">
        {select('agentId', tx('Yedekleme bilgisayarı'), agentId, [['', tx('Yedekleme bilgisayarı seçin')], ...(agents.data ?? []).map(item => [item.agentId, `${item.hostName ?? tx('Adsız bilgisayar')} · ${tx(item.operationalStatus)}`] as const)], onAgentId)}
        {agents.isPending && <p role="status" className="text-sm text-text-muted">{tx("Bilgisayar listesi yükleniyor…")}</p>}{agents.error && <p role="alert" className="text-sm text-error">{errorMessage(agents.error)}</p>}
      </div>
    </FormGrid>
  </Question>
}
