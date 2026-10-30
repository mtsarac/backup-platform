import { tx, useLanguage } from '@/lib/i18n'
import { RadioField } from '@/components/ui/radio-field'
import { Select } from '@/components/ui/text-input'
import { CheckCircle2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { FilePathList } from '@/features/backup-jobs/file-path-list'
import type { RestoreTargetAgent, RestoreTargetsResponse } from './types'

interface FileRestoreSourceStepProps {
  instancePrefix: string
  targets: RestoreTargetsResponse
  agent: RestoreTargetAgent | undefined
  agentId: string
  /** Only the backup computer that made the backup can restore to the original location. */
  originalAgent: RestoreTargetAgent | undefined
  original: boolean
  scope: 'all' | 'selected'
  paths: string[]
  pathsValid: boolean
  eligible: boolean
  onAgentChange: (agentId: string) => void
  onScopeChange: (scope: 'all' | 'selected') => void
  onPathsChange: (paths: string[]) => void
  onContinue: () => void
}

/** The backup file is fixed by the page; here the user picks the computer that reads it and which of its files to bring back. */
export function FileRestoreSourceStep({ instancePrefix, targets, agent, agentId, originalAgent, original, scope, paths, pathsValid, eligible, onAgentChange, onScopeChange, onPathsChange, onContinue }: FileRestoreSourceStepProps) {
  useLanguage()
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
          {agent && !eligible && <ErrorNotice failureCode={agent.rejectionCode} scope="restore" />}
          {agent && <TechnicalDetails items={[
            { label: tx('Yedekleme bilgisayarı kimliği'), value: agent.agentId },
            ...(agent.sourceAccess ? [{ label: tx('Yedek erişim yolu'), value: agent.sourceAccess }] : []),
          ]} />}
        </div>
      </Section>

      <Section title={tx("Hangi dosyalar geri yüklensin?")} description={tx("Neden soruluyor? Yedeğin tamamını ya da yalnızca ihtiyacınız olan dosya ve klasörleri geri yükleyebilirsiniz.")}>
        <div className="space-y-4">
          <fieldset className="flex flex-wrap gap-4">
            <legend className="sr-only">{tx("Geri yükleme kapsamı")}</legend>
            <RadioField label={tx("Tüm dosya ve klasörler")} name={`${instancePrefix}-scope`} checked={scope === 'all'} onChange={() => onScopeChange('all')} />
            <RadioField label={tx("Seçili dosya ve klasörler")} name={`${instancePrefix}-scope`} checked={scope === 'selected'} onChange={() => onScopeChange('selected')} />
          </fieldset>
          {scope === 'selected' && <FilePathList id={`${instancePrefix}-paths`} label={tx('Geri yüklenecek göreli yollar')} paths={paths} onChange={onPathsChange} error={!pathsValid ? tx('1–256 geçerli göreli yol girin.') : undefined} />}
          <p className="text-sm text-text-muted">{tx("Seçilen klasörlerin altındaki dosyalar da geri yüklenir; arşivdeki göreli dizin yapısı korunur. Arşivde bulunmayan seçimler işlemi durdurur.")}</p>
        </div>
      </Section>

      <div className="flex justify-end gap-3">
        <Button disabled={!eligible || !pathsValid} onClick={() => onContinue()}>{tx("Devam et →")}</Button>
      </div>
    </div>
  )
}
