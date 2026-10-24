import { tx, useLanguage } from '@/lib/i18n'
import { CheckCircle2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import type { RestoreTargetAgent, RestoreTargetsResponse } from './types'

interface FileRestoreTargetStepProps {
  instancePrefix: string
  targets: RestoreTargetsResponse
  agent: RestoreTargetAgent | undefined
  agentId: string
  /** Only the backup computer that made the backup can restore to the original location. */
  originalAgent: RestoreTargetAgent | undefined
  original: boolean
  reference: string
  directory: string
  referenceValid: boolean
  directoryValid: boolean
  eligible: boolean
  onAgentChange: (agentId: string) => void
  onLocationChange: (location: 'original' | 'alternate') => void
  onReferenceChange: (value: string) => void
  onDirectoryChange: (value: string) => void
  onContinue: () => void
}

export function FileRestoreTargetStep({ instancePrefix, targets, agent, agentId, originalAgent, original, reference, directory, referenceValid, directoryValid, eligible, onAgentChange, onLocationChange, onReferenceChange, onDirectoryChange, onContinue }: FileRestoreTargetStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("Hangi yedekleme bilgisayarı geri yüklesin?")} description={tx("Neden soruluyor? Dosyaları yedekten alıp yerlerine koyacak yedekleme bilgisayarını seçmeniz gerekir.")}>
        <div className="space-y-3">
          <label htmlFor={`${instancePrefix}-agent`} className="block text-sm font-medium">{tx("Yedekleme bilgisayarı")}</label>
          <select id={`${instancePrefix}-agent`} className={`${selectClass} w-full`} value={agentId} onChange={(event) => onAgentChange(event.target.value)}>
            <option value="">{tx("Yedekleme bilgisayarı seçin")}</option>
            {targets.agents.map((item) => (
              <option key={item.agentId} value={item.agentId} disabled={!item.canRestore || !item.canPrecheck || !item.sourceEligible || (original && item.agentId !== originalAgent?.agentId)}>{item.hostName ?? item.agentId}</option>
            ))}
          </select>
          {eligible && <p className="flex items-center gap-2 text-sm text-success"><CheckCircle2 aria-hidden="true" className="size-4 shrink-0" />{tx("Bu bilgisayar bu yedek dosyasına ulaşabilir.")}</p>}
          {agent && !eligible && <ErrorNotice failureCode={agent.rejectionCode} scope="restore" />}
          {agent && <TechnicalDetails items={[
            { label: tx('Yedekleme bilgisayarı kimliği'), value: agent.agentId },
            ...(agent.sourceAccess ? [{ label: tx('Yedek erişim yolu'), value: agent.sourceAccess }] : []),
          ]} />}
        </div>
      </Section>

      <Section title={tx("Dosyalar nereye geri konsun?")} description={tx("Neden soruluyor? Dosyalar ya yedeklendikleri yere geri döner ya da başka bir klasöre konur; ikincisinde mevcut dosyalara dokunulmaz.")}>
        <div className="space-y-4">
          <fieldset className="space-y-3">
            <legend className="sr-only">{tx("Geri yükleme konumu")}</legend>
            <div>
              <label className="flex items-center gap-2 text-sm"><input type="radio" name={`${instancePrefix}-location`} aria-describedby={`${instancePrefix}-original-hint`} checked={original} disabled={!originalAgent} onChange={() => onLocationChange('original')} />{tx("Özgün konum")}</label>
              <p id={`${instancePrefix}-original-hint`} className="ml-6 text-sm text-text-muted">{tx("Dosyalar yedeklendikleri yere geri konur. Bunun için yedeği alan bilgisayar ve değişmemiş kaynak eşlemesi gerekir.")}</p>
            </div>
            <div>
              <label className="flex items-center gap-2 text-sm"><input type="radio" name={`${instancePrefix}-location`} aria-describedby={`${instancePrefix}-alternate-hint`} checked={!original} onChange={() => onLocationChange('alternate')} />{tx("Alternatif konum")}</label>
              <p id={`${instancePrefix}-alternate-hint`} className="ml-6 text-sm text-text-muted">{tx("Dosyalar başka bir klasöre konur; yedeklendikleri yerdeki dosyalara dokunulmaz.")}</p>
            </div>
          </fieldset>
          {!originalAgent && <p className="text-sm text-text-muted">{tx("Özgün konum için kaynak işin Agent eşlemesi ve uygun bir Agent gerekir. Alternatif konum kullanabilirsiniz.")}</p>}
          {original ? <p className="text-sm">{tx("Dosya kaynağı referansı")}: <code>{targets.sourceDatabase}</code></p> : (
            <div className="grid gap-4 sm:grid-cols-2">
              <Field id={`${instancePrefix}-reference`} label={tx('Hedef dosya kaynağı referansı')} required error={!referenceValid ? tx('Geçerli bir dosya kaynağı referansı girin.') : undefined}>{(control) => <input {...control} className={inputClass} value={reference} onChange={(event) => onReferenceChange(event.target.value)} />}</Field>
              <Field id={`${instancePrefix}-directory`} label={tx('Hedef göreli klasör')} hint={tx('Boş değer veya . yapılandırılmış kökü kullanır; örnek: restored/2026.')} error={!directoryValid ? tx('Geçerli bir göreli yol girin.') : undefined}>{(control) => <input {...control} className={inputClass} value={directory} onChange={(event) => onDirectoryChange(event.target.value)} />}</Field>
            </div>
          )}
        </div>
      </Section>

      <div className="flex justify-end gap-3">
        <Button disabled={!eligible || !referenceValid || !directoryValid} onClick={() => onContinue()}>{tx("Devam et →")}</Button>
      </div>
    </div>
  )
}
