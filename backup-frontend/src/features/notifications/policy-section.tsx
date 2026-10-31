import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import type { FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { CheckField } from '@/components/ui/check-field'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useNotificationPolicy, useUpdateNotificationPolicy } from './hooks'
import { eventTypeLabels, kindLabels, severityLabels } from './labels'
import { thresholdRanges, toThresholds, validateThresholds } from './policy-validation'
import type { ThresholdField } from './policy-validation'
import { severities, visibleChannelKinds, visibleEventTypes } from './types'
import type { ChannelKind, EventType, NotificationPolicy, Severity } from './types'
import { formatDateTime } from '@/lib/format'
import { Select } from '@/components/ui/text-input'

const thresholdLabels: Record<ThresholdField, string> = {
  get staleAfterHours() { return tx("Backup güncel değil eşiği (saat)") },
  get agentOfflineAfterMinutes() { return tx("Agent çevrimdışı eşiği (dakika)") },
  get reminderIntervalHours() { return tx("Hatırlatma aralığı (saat, 0 = hatırlatma yok)") },
  get maxNotificationsPerHour() { return tx("Saatlik azami bildirim") },
}

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
  const unavailable = visibleChannelKinds().filter((kind) => kinds.includes(kind) && kind !== 'webhook' && !policy.transports[kind].available)

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitted(true)
    if (Object.keys(errors).length > 0) return
    save.mutate({ enabled, eventTypes: events, minimumSeverity, channelKinds: kinds, recoveryNotifications: recovery, ...toThresholds(thresholds) })
  }

  return <form onSubmit={submit} noValidate className="space-y-6">
    <CheckField role="switch" className="font-medium" label={tx("Bildirimler etkin")} checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />
    <fieldset className="space-y-2"><legend className="text-sm font-medium text-text-heading">{tx("Olay türleri")}</legend>
      <div className="flex flex-wrap gap-x-6">{visibleEventTypes().map((type) => <CheckField key={type} label={eventTypeLabels[type]} checked={events.includes(type)} onChange={(event) => setEvents(toggle(events, type, event.target.checked))} />)}</div>
      <p className="text-sm text-text-muted">{tx("Bir platform yöneticisi şifreli yedeklerinize acil destek erişimi kullandığında kritik uyarı gönderilir. Bu olay kapatılamaz; genel kanallara gönderim için bildirimlerin etkin ve en az bir kanalın hazır olması gerekir. Sahiplerin kayıtlı güvenlik adresleri ayrıca her zaman bilgilendirilir.")}</p>
    </fieldset>
    <fieldset className="space-y-2"><legend className="text-sm font-medium text-text-heading">{tx("Kanal türleri")}</legend>
      <div className="flex flex-wrap gap-x-6">{visibleChannelKinds().map((kind) => <CheckField key={kind} label={kindLabels[kind]} checked={kinds.includes(kind)} onChange={(event) => setKinds(toggle(kinds, kind, event.target.checked))} />)}</div>
      {unavailable.map((kind) => <Notice key={kind} tone="warning">{tx("{0} aktarımı platform tarafından yapılandırılmamış veya etkin değil; bu tür seçili olsa da bildirim gönderilemez.", { '0': kindLabels[kind] })}</Notice>)}
    </fieldset>
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label={tx("Asgari önem düzeyi")}>
        {(control) => <Select {...control} value={minimumSeverity} onChange={(event) => setMinimumSeverity(event.target.value as Severity)}>{severities.map((severity) => <option key={severity} value={severity}>{severityLabels[severity]}</option>)}</Select>}
      </Field>
      <div className="flex items-end"><CheckField label={tx("Düzelme bildirimleri gönder")} checked={recovery} onChange={(event) => setRecovery(event.target.checked)} /></div>
    </div>
    <TechnicalDetails accordion defaultOpen={submitted && Object.keys(errors).length > 0} items={[
      ...(policy.updatedAt ? [{ label: tx('Son güncelleme'), value: formatDateTime(policy.updatedAt) }] : []),
    ]}>
      <div className="grid gap-4 sm:grid-cols-2">{(Object.keys(thresholdRanges) as ThresholdField[]).map((field) => <Field key={field} label={thresholdLabels[field]} error={submitted ? errors[field] : undefined}>
        {(control) => <TextInput {...control} inputMode="numeric" value={thresholds[field]} onChange={(event) => setThresholds({ ...thresholds, [field]: event.target.value })} />}
      </Field>)}</div>
    </TechnicalDetails>
    {save.isError && <ErrorNotice error={save.error} />}
    {save.isSuccess && <Notice tone="success">{tx("Politika kaydedildi.")}</Notice>}
    <div className="flex flex-wrap items-center gap-3 border-t border-border pt-4"><Button type="submit" disabled={save.isPending}>{save.isPending ? tx('Kaydediliyor…') : tx('Politikayı kaydet')}</Button>
      {!policy.updatedAt && <span className="text-sm text-text-muted">{tx('Henüz kaydedilmedi; varsayılan değerler gösteriliyor.')}</span>}</div>
  </form>
}

export function PolicySection() {
  useLanguage()
  const policy = useNotificationPolicy()
  return <Section title={tx("Bildirim politikası")} description={tx("Hangi olayların, hangi önem düzeyinden itibaren, hangi kanal türlerine bildirileceği.")}>
    <QueryState isPending={policy.isPending} error={policy.error}>{policy.data && <PolicyForm policy={policy.data} />}</QueryState>
  </Section>
}
