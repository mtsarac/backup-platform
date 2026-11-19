import { tx, useLanguage } from '@/lib/i18n'
import { Select } from '@/components/ui/text-input'
import { CheckCircle2 } from 'lucide-react'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useTenant } from '@/features/tenancy/use-tenant'
import { actionLabel, describeFailure } from '@/lib/api/failure-messages'
import type { RestoreTargetAgent, RestoreTargetsResponse } from './types'

interface FileRestoreSourceStepProps {
  instancePrefix: string
  targets: RestoreTargetsResponse
  agent: RestoreTargetAgent | undefined
  agentId: string
  /** Only the backup computer that made the backup can restore to the original location. */
  originalAgent: RestoreTargetAgent | undefined
  original: boolean
  eligible: boolean
  onAgentChange: (agentId: string) => void
  onContinue: () => void
}

/** The backup file is fixed by the page; here the user picks the computer that reads it. Skipped when only one computer fits. */
export function FileRestoreSourceStep({ instancePrefix, targets, agent, agentId, originalAgent, original, eligible, onAgentChange, onContinue }: FileRestoreSourceStepProps) {
  useLanguage()
  const { tenantId } = useTenant()
  return (
    <div className="space-y-5">
      <Section title={tx("Hangi yedekleme bilgisayarı geri yüklesin?")} description={tx("Neden soruluyor? Dosyaları yedekten alıp yerlerine koyacak yedekleme bilgisayarını seçmeniz gerekir.")}>
        <div className="space-y-3">
          <label htmlFor={`${instancePrefix}-agent`} className="block text-sm font-medium">{tx("Yedekleme bilgisayarı")}</label>
          <Select id={`${instancePrefix}-agent`} value={agentId} onChange={(event) => onAgentChange(event.target.value)}>
            <option value="">{tx("Yedekleme bilgisayarı seçin")}</option>
            {targets.agents.map((item) => (
              <option key={item.agentId} value={item.agentId} disabled={!item.canRestore || !item.canPrecheck || !item.sourceEligible || (original && item.agentId !== originalAgent?.agentId)}>{item.hostName ?? item.agentId}</option>
            ))}
          </Select>
          {eligible && <p className="flex items-center gap-2 text-sm text-success"><CheckCircle2 aria-hidden="true" className="size-4 shrink-0" />{tx("Bu bilgisayar bu yedek dosyasına ulaşabilir.")}</p>}
          {agent && !eligible && <ErrorNotice failureCode={agent.rejectionCode} scope="restore" control={describeFailure(agent.rejectionCode, 'restore').actionKind === 'checkComputer' ? <Link className={buttonVariants({ variant: 'outline' })} to={`/tenants/${tenantId}/agents/${agent.agentId}`}>{actionLabel('checkComputer')}</Link> : undefined} />}
          {agent && <TechnicalDetails items={[
            { label: tx('Yedekleme bilgisayarı kimliği'), value: agent.agentId },
            ...(agent.sourceAccess ? [{ label: tx('Yedek erişim yolu'), value: agent.sourceAccess }] : []),
          ]} />}
        </div>
      </Section>

      <div className="flex justify-end gap-3">
        <Button disabled={!eligible} onClick={() => onContinue()}>{tx("Devam et →")}</Button>
      </div>
    </div>
  )
}
