import { tx, useLanguage } from '@/lib/i18n'
import { CheckCircle2, XCircle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { selectClass } from '@/components/ui/form-styles'
import type { RestoreTargetAgent, RestoreTargetsResponse } from './types'
import { failureMessage } from '@/lib/api/failure-messages'

interface RestoreSourceStepProps {
  instancePrefix: string
  engineName: string
  targets: RestoreTargetsResponse
  activeAgent: RestoreTargetAgent | undefined
  selectedAgentId: string
  eligibleForRestore: boolean
  invalidateDiscovery: () => void
  setSelectedAgentId: (id: string) => void
  onContinue: () => void
}

/** The backup file is fixed by the page; what is chosen here is the backup computer that can read it. */
export function RestoreSourceStep({ instancePrefix, engineName, targets, activeAgent, selectedAgentId, eligibleForRestore, invalidateDiscovery, setSelectedAgentId, onContinue }: RestoreSourceStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Hangi yedekleme bilgisayarı geri yüklesin?")} description={tx("Neden soruluyor? Yedek dosyasını alıp {0} sunucusuna aktaracak yedekleme bilgisayarını seçmeniz gerekir.", { "0": engineName })}>
        <div className="space-y-3">
          <label htmlFor={`${instancePrefix}-agent`} className="block text-sm font-medium">{tx("Yedekleme bilgisayarı")}</label>
          <select
            id={`${instancePrefix}-agent`}
            className={`${selectClass} w-full`}
            value={selectedAgentId}
            onChange={(e) => { invalidateDiscovery(); setSelectedAgentId(e.target.value) }}
          >
            {targets.agents.map((ag) => (
              <option key={ag.agentId} value={ag.agentId}>
                {ag.hostName ?? ag.agentId} ({tx(ag.connectivity)}) — {ag.canRestore ? tx('Geri yüklemeyi destekliyor') : tx('Geri yükleme desteği yok')}
              </option>
            ))}
          </select>

          {activeAgent && (
            <div className={`rounded-md p-3 text-sm ${activeAgent.sourceEligible && activeAgent.canRestore ? 'bg-success-subtle text-success' : 'bg-error-subtle text-error'}`}>
              {activeAgent.sourceEligible && activeAgent.canRestore ? (
                <div className="flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <CheckCircle2 aria-hidden="true" className="size-4 shrink-0" />
                    <span>{tx("Bu bilgisayar bu yedek dosyasına ulaşabilir.")}</span>
                  </div>
                  <p className="ml-6 text-xs text-text-muted">{activeAgent.sourceAccess === 'AgentLocal'
                    ? tx('Yedek yalnızca bu bilgisayarın kendi diskinde olduğu için geri yüklemeyi başka bir bilgisayar yapamaz.')
                    : tx('Bilgisayar veya hedef değişirse geri yükleme kendiliğinden yeniden başlamaz.')}</p>
                </div>
              ) : (
                <div className="flex items-start gap-2">
                  <XCircle aria-hidden="true" className="size-4 shrink-0 mt-0.5" />
                  <div>
                    <p className="font-medium">{tx("Bu bilgisayar seçilemez veya yedek dosyasına ulaşamaz:")}</p>
                    <p className="mt-1 text-sm">{failureMessage(activeAgent.rejectionCode, 'restore')}</p>
                  </div>
                </div>
              )}
            </div>
          )}
          {activeAgent && <TechnicalDetails items={[
            { label: tx('Yedekleme bilgisayarı kimliği'), value: activeAgent.agentId },
            ...(activeAgent.sourceAccess ? [{ label: tx('Yedek erişim yolu'), value: activeAgent.sourceAccess }] : []),
          ]} />}
        </div>
      </Section>

      <div className="flex justify-end gap-3">
        <Button disabled={!eligibleForRestore} onClick={() => onContinue()}>{tx("Devam et →")}</Button>
      </div>
    </div>
  )
}
