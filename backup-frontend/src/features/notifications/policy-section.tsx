import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import type { FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { CheckField } from '@/components/ui/check-field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { useNotificationPolicy, useUpdateNotificationPolicy } from './hooks'
import { visibleChannelKinds, visibleEventTypes } from './types'
import type { NotificationPolicy } from './types'

function PolicyForm({ policy }: { policy: NotificationPolicy }) {
  useLanguage()
  const save = useUpdateNotificationPolicy()
  const [enabled, setEnabled] = useState(policy.enabled)
  const [recovery, setRecovery] = useState(policy.recoveryNotifications)
  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate({ enabled, eventTypes: visibleEventTypes().filter(type => type !== 'backup_size_anomaly'), minimumSeverity: 'warning', channelKinds: visibleChannelKinds().filter(kind => kind !== 'sms'), recoveryNotifications: recovery, staleAfterHours: 48, agentOfflineAfterMinutes: 15, reminderIntervalHours: 24, maxNotificationsPerHour: 30 })
  }

  return <form onSubmit={submit} noValidate className="space-y-6">
    <CheckField role="switch" className="font-medium" label={tx("Bildirimler etkin")} checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />
    <CheckField label={tx("Düzelme bildirimleri gönder")} checked={recovery} onChange={(event) => setRecovery(event.target.checked)} />
    {save.isError && <ErrorNotice error={save.error} />}
    {save.isSuccess && <Notice tone="success">{tx("Politika kaydedildi.")}</Notice>}
    <div className="flex flex-wrap items-center gap-3 border-t border-border pt-4"><Button type="submit" disabled={save.isPending}>{save.isPending ? tx('Kaydediliyor…') : tx('Politikayı kaydet')}</Button>
      {!policy.updatedAt && <span className="text-sm text-text-muted">{tx('Henüz kaydedilmedi; varsayılan değerler gösteriliyor.')}</span>}</div>
  </form>
}

export function PolicySection() {
  useLanguage()
  const policy = useNotificationPolicy()
  return <Section title={tx("Bildirim politikası")}>
    <QueryState isPending={policy.isPending} error={policy.error}>{policy.data && <PolicyForm policy={policy.data} />}</QueryState>
  </Section>
}
