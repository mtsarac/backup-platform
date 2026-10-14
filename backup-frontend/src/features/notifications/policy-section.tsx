import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { QueryState } from '@/components/data/query-state'
import { agentDate } from '@/features/agents/format'
import { inputClass, selectClass } from '@/features/platform/styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { useNotificationPolicy, useUpdateNotificationPolicy } from './hooks'
import { eventTypeLabels, kindLabels, severityLabels } from './labels'
import { thresholdRanges, toThresholds, validateThresholds, type ThresholdField } from './policy-validation'
import { channelKinds, eventTypes, severities, type ChannelKind, type EventType, type NotificationPolicy, type Severity } from './types'

const thresholdLabels: Record<ThresholdField, string> = {
  staleAfterHours: 'Backup güncel değil eşiği (saat)',
  agentOfflineAfterMinutes: 'Agent çevrimdışı eşiği (dakika)',
  reminderIntervalHours: 'Hatırlatma aralığı (saat, 0 = hatırlatma yok)',
  maxNotificationsPerHour: 'Saatlik azami bildirim',
}

function toggle<T>(list: T[], item: T, on: boolean) {
  return on ? [...list, item] : list.filter((entry) => entry !== item)
}

function PolicyForm({ policy }: { policy: NotificationPolicy }) {
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

  return <form onSubmit={submit} noValidate className="space-y-4">
    <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />Bildirimler etkin</label>
    <fieldset className="space-y-1"><legend className="text-sm text-text-muted">Olay türleri</legend>
      <div className="flex flex-wrap gap-x-4 gap-y-1">{eventTypes.map((type) => <label key={type} className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={events.includes(type)} onChange={(event) => setEvents(toggle(events, type, event.target.checked))} />{eventTypeLabels[type]}</label>)}</div>
    </fieldset>
    <label className="block text-sm text-text-muted">Asgari önem düzeyi
      <select className={selectClass} value={minimumSeverity} onChange={(event) => setMinimumSeverity(event.target.value as Severity)}>{severities.map((severity) => <option key={severity} value={severity}>{severityLabels[severity]}</option>)}</select>
    </label>
    <fieldset className="space-y-1"><legend className="text-sm text-text-muted">Kanal türleri</legend>
      <div className="flex flex-wrap gap-x-4 gap-y-1">{channelKinds.map((kind) => <label key={kind} className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={kinds.includes(kind)} onChange={(event) => setKinds(toggle(kinds, kind, event.target.checked))} />{kindLabels[kind]}</label>)}</div>
      {unavailable.map((kind) => <p key={kind} role="alert" className="text-xs text-warning">{kindLabels[kind]} aktarımı platform tarafından yapılandırılmamış veya etkin değil; bu tür seçili olsa da bildirim gönderilemez.</p>)}
    </fieldset>
    <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={recovery} onChange={(event) => setRecovery(event.target.checked)} />Düzelme bildirimleri gönder</label>
    <div className="grid gap-4 sm:grid-cols-2">{(Object.keys(thresholdRanges) as ThresholdField[]).map((field) => <label key={field} className="block text-sm text-text-muted">{thresholdLabels[field]}
      <input className={inputClass} inputMode="numeric" value={thresholds[field]} aria-invalid={submitted && Boolean(errors[field])} onChange={(event) => setThresholds({ ...thresholds, [field]: event.target.value })} />
      {submitted && errors[field] && <span role="alert" className="text-xs text-error">{errors[field]}</span>}
    </label>)}</div>
    {save.isError && <p role="alert" className="text-sm text-error">{errorMessage(save.error)}</p>}
    {save.isSuccess && <p role="status" className="text-sm text-success">Politika kaydedildi.</p>}
    <div className="flex flex-wrap items-center gap-3"><Button type="submit" disabled={save.isPending}>{save.isPending ? 'Kaydediliyor…' : 'Politikayı kaydet'}</Button>
      <span className="text-xs text-text-muted">{policy.updatedAt ? `Son güncelleme: ${agentDate(policy.updatedAt)}` : 'Henüz kaydedilmedi; varsayılan değerler gösteriliyor.'}</span></div>
  </form>
}

export function PolicySection() {
  const policy = useNotificationPolicy()
  return <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="notification-policy-heading">
    <div><h2 id="notification-policy-heading" className="font-medium text-text-heading">Bildirim politikası</h2>
      <p className="mt-1 text-sm text-text-muted">Hangi olayların, hangi önem düzeyinden itibaren, hangi kanal türlerine bildirileceği.</p></div>
    <QueryState isPending={policy.isPending} error={policy.error}>{policy.data && <PolicyForm policy={policy.data} />}</QueryState>
  </section>
}
