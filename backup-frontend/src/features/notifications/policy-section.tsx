import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import type { FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { inputClass, selectClass } from '@/features/platform/styles'
import { useNotificationPolicy, useUpdateNotificationPolicy } from './hooks'
import { eventTypeLabels, kindLabels, severityLabels } from './labels'
import { thresholdRanges, toThresholds, validateThresholds } from './policy-validation'
import type { ThresholdField } from './policy-validation'
import { channelKinds, eventTypes, severities } from './types'
import type { ChannelKind, EventType, NotificationPolicy, Severity } from './types'
import { formatDateTime } from '@/lib/format'

const thresholdLabels: Record<ThresholdField, string> = {
  get staleAfterHours() { return tx("Backup güncel değil eşiği (saat)") },
  get agentOfflineAfterMinutes() { return tx("Agent çevrimdışı eşiği (dakika)") },
  get reminderIntervalHours() { return tx("Hatırlatma aralığı (saat, 0 = hatırlatma yok)") },
  get maxNotificationsPerHour() { return tx("Saatlik azami bildirim") },
}

const checkClass = 'flex min-h-11 items-center gap-3 rounded-lg border border-border bg-surface-2 px-3 text-[0.9375rem] text-text'
const boxClass = 'size-5 accent-primary'

function toggle<T>(list: T[], item: T, on: boolean) {
  return on ? [...list, item] : list.filter((entry) => entry !== item)
}

function PolicyForm({ policy }: { policy: NotificationPolicy }) {
  useLanguage()
  const save = useUpdateNotificationPolicy()
  const [enabled, setEnabled] = useState(policy.enabled)
  const [events, setEvents] = useState<EventType[]>(policy.eventTypes)
  const [minimumSeverity, setMinimumSeverity] = useState<Severity>(policy.minimumSeverity)
  const [kinds, setKinds] = useState<ChannelKind[]>(policy.channelKinds)
  const [recovery, setRecovery] = useState(policy.recoveryNotifications)
  const [thresholds, setThresholds] = useState<Record<ThresholdField, string>>({
    staleAfterHours: String(policy.staleAfterHours), agentOfflineAfterMinutes: String(policy.agentOfflineAfterMinutes),
    reminderIntervalHours: String(policy.reminderIntervalHours), maxNotificationsPerHour: String(policy.maxNotificationsPerHour),
  })
  const [submitted, setSubmitted] = useState(false)
  const errors = validateThresholds(thresholds)
  const unavailable = kinds.filter((kind) => kind !== 'webhook' && !policy.transports[kind].available)

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (Object.keys(errors).length > 0) return
    save.mutate({ enabled, eventTypes: events, minimumSeverity, channelKinds: kinds, recoveryNotifications: recovery, ...toThresholds(thresholds) })
  }

  return <form onSubmit={submit} noValidate className="space-y-6">
    <label className={`${checkClass} font-medium`}><input type="checkbox" className={boxClass} checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />{tx("Bildirimler etkin")}</label>
    <fieldset className="space-y-2"><legend className="text-sm font-medium text-text-heading">{tx("Olay türleri")}</legend>
      <div className="flex flex-wrap gap-2">{eventTypes.map((type) => <label key={type} className={checkClass}><input type="checkbox" className={boxClass} checked={events.includes(type)} onChange={(event) => setEvents(toggle(events, type, event.target.checked))} />{eventTypeLabels[type]}</label>)}</div>
      <p className="text-sm text-text-muted">{tx("Break-glass erişimi bildirimi zorunludur ve kapatılamaz: bir platform yöneticisi şifreli Backup'larınız için break-glass erişimi kullandığında Kritik olarak gönderilir (bildirimler etkinse ve en az bir kanal varsa).")}</p>
    </fieldset>
    <fieldset className="space-y-2"><legend className="text-sm font-medium text-text-heading">{tx("Kanal türleri")}</legend>
      <div className="flex flex-wrap gap-2">{channelKinds.map((kind) => <label key={kind} className={checkClass}><input type="checkbox" className={boxClass} checked={kinds.includes(kind)} onChange={(event) => setKinds(toggle(kinds, kind, event.target.checked))} />{kindLabels[kind]}</label>)}</div>
      {unavailable.map((kind) => <Notice key={kind} tone="warning">{kindLabels[kind]}{' '}{tx("aktarımı platform tarafından yapılandırılmamış veya etkin değil; bu tür seçili olsa da bildirim gönderilemez.")}</Notice>)}
    </fieldset>
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label={tx("Asgari önem düzeyi")}>
        {(control) => <select {...control} className={`${selectClass} w-full`} value={minimumSeverity} onChange={(event) => setMinimumSeverity(event.target.value as Severity)}>{severities.map((severity) => <option key={severity} value={severity}>{severityLabels[severity]}</option>)}</select>}
      </Field>
      <div className="flex items-end"><label className={checkClass}><input type="checkbox" className={boxClass} checked={recovery} onChange={(event) => setRecovery(event.target.checked)} />{tx("Düzelme bildirimleri gönder")}</label></div>
    </div>
    <div className="grid gap-4 sm:grid-cols-2">{(Object.keys(thresholdRanges) as ThresholdField[]).map((field) => <Field key={field} label={thresholdLabels[field]} error={submitted ? errors[field] : undefined}>
      {(control) => <input {...control} className={inputClass} inputMode="numeric" value={thresholds[field]} onChange={(event) => setThresholds({ ...thresholds, [field]: event.target.value })} />}
    </Field>)}</div>
    {save.isError && <ErrorNotice error={save.error} />}
    {save.isSuccess && <Notice tone="success">{tx("Politika kaydedildi.")}</Notice>}
    <div className="flex flex-wrap items-center gap-3 border-t border-border pt-4"><Button type="submit" disabled={save.isPending}>{save.isPending ? tx('Kaydediliyor…') : tx('Politikayı kaydet')}</Button>
      <span className="text-sm text-text-muted">{policy.updatedAt ? tx("Son güncelleme: {0}", { "0": formatDateTime(policy.updatedAt, tx('Henüz bildirilmedi')) }) : tx('Henüz kaydedilmedi; varsayılan değerler gösteriliyor.')}</span></div>
  </form>
}

export function PolicySection() {
  useLanguage()
  const policy = useNotificationPolicy()
  return <Section title={tx("Bildirim politikası")} description={tx("Hangi olayların, hangi önem düzeyinden itibaren, hangi kanal türlerine bildirileceği.")}>
    <QueryState isPending={policy.isPending} error={policy.error}>{policy.data && <PolicyForm policy={policy.data} />}</QueryState>
  </Section>
}
