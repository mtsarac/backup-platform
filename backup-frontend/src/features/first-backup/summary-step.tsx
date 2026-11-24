import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice } from '@/components/ui/notice'
import { Select } from '@/components/ui/text-input'
import { keepForLabel, retentionPresets, schedulePresetLabel, schedulePresets } from '@/features/backup-jobs/job-wizard-model'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { connectionOptions } from '@/features/storage-connections/job-connection-picker-utils'
import { connectionDisplayName } from '@/features/storage-connections/labels'
import { EncryptionSwitch } from '@/features/artifact-encryption/encryption-switch'
import { encryptionStateLabel } from '@/features/artifact-encryption/labels'
import { useDestinationLabel } from './use-destination-label'
import { autoJobName, type FirstBackupItem, type FirstBackupSettings } from './first-backup-model'
import { actionName } from '@/lib/accessible-name'

export type ItemResult = { jobId?: string; runId?: string; error?: unknown }
type Row = 'where' | 'when' | 'keep' | 'encryption'

/** "Özet ve başla": where, when, how long and encryption, each with "Değiştir". Nothing is created before one of the two buttons. */
export function SummaryStep({ items, names, settings, onSettings, results, pending, onCreate, onBack }: {
  items: readonly FirstBackupItem[]; names: Record<string, string>; settings: FirstBackupSettings; onSettings: (settings: FirstBackupSettings) => void
  results: Record<string, ItemResult>; pending: boolean; onCreate: (startFirstBackup: boolean) => void; onBack: () => void
}) {
  useLanguage()
  const connections = useTenantConnections()
  const providers = useTenantProviders()
  const [editing, setEditing] = useState<Row | null>(null)
  const connectionList = connections.data?.items ?? []
  const providerList = providers.data?.items ?? []
  const destination = settings.destinations[0]
  const where = useDestinationLabel(settings)
  const when = settings.scheduled ? schedulePresetLabel(settings.cronExpression) ?? tx('Özel zamanlama') : tx('Elle çalıştırılır (zamanlama yok)')
  const keep = settings.retention.enabled ? keepForLabel(settings.retention.keepForSeconds) : tx('Hiç silinmesin')
  const row = (id: Row, label: string, value: string, editor: React.ReactNode) => <div key={id} className="space-y-2 px-4 py-3">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div><dt className="text-sm text-text-muted">{label}</dt><dd className="font-medium text-text-heading">{value}</dd></div>
      <Button type="button" variant="outline" size="sm" aria-label={actionName(tx("Değiştir"), label)} aria-expanded={editing === id} onClick={() => setEditing(editing === id ? null : id)}>{tx('Değiştir')}</Button>
    </div>
    {editing === id && editor}
  </div>
  // The empty option already is the local default connection; the system defaults read by their plain name alone.
  const options = connectionOptions(connectionList, providerList).filter(option => !option.unavailable).flatMap(option => {
    const connection = connectionList.find(item => item.id === option.id)!
    if (connection.isDefault && connection.providerType === 'local') return []
    const name = connectionDisplayName(connection)
    return [name === connection.name ? option : { ...option, label: name }]
  })
  const failed = items.filter(item => results[item.key]?.error)
  const done = (item: FirstBackupItem) => results[item.key]?.jobId
  return <section aria-labelledby="first-backup-summary" className="space-y-4">
    <h2 id="first-backup-summary" className="text-lg font-semibold text-text-heading">{tx('Özet ve başla')}</h2>
    <dl className="divide-y divide-border rounded-lg border border-border bg-surface">
      {row('where', tx('Nereye'), where, <Field label={tx('Depolama')}>{control => <Select {...control} value={destination.storageConnectionId ?? ''} onChange={event => {
        const picked = connectionList.find(item => item.id === event.target.value)
        const location = providerList.find(item => item.providerType === picked?.providerType)?.executionLocation
        onSettings({ ...settings, destinations: picked && location
          ? [{ ...destination, storageProvider: picked.providerType, storageConnectionId: picked.id, storageExecutionLocation: location }]
          : [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }] })
      }}>
        <option value="">{tx('Yerel disk')}</option>
        {options.map(option => <option key={option.id} value={option.id}>{option.label}</option>)}
      </Select>}</Field>)}
      {row('when', tx('Ne zaman'), when, <Field label={tx('Hazır zamanlamalar')}>{control => <Select {...control} value={settings.scheduled ? settings.cronExpression : 'manual'} onChange={event => onSettings(event.target.value === 'manual' ? { ...settings, scheduled: false } : { ...settings, scheduled: true, cronExpression: event.target.value })}>
        {schedulePresets.map(([label, cron]) => <option key={cron} value={cron}>{tx(label)}</option>)}
        <option value="manual">{tx('Elle çalıştır')}</option>
      </Select>}</Field>)}
      {row('keep', tx('Saklama'), keep, <Field label={tx('Saklama süresi')}>{control => <Select {...control} value={settings.retention.enabled ? String(settings.retention.keepForSeconds) : 'off'} onChange={event => onSettings({ ...settings, retention: event.target.value === 'off' ? { ...settings.retention, enabled: false } : { ...settings.retention, enabled: true, keepForSeconds: Number(event.target.value) } })}>
        {retentionPresets.map(([seconds]) => <option key={seconds} value={seconds}>{keepForLabel(seconds)}</option>)}
        <option value="off">{tx('Hiç silinmesin')}</option>
      </Select>}</Field>)}
      {row('encryption', tx('Şifreleme'), encryptionStateLabel(settings.encryptionPolicy), <EncryptionSwitch policy={settings.encryptionPolicy} onChange={encryptionPolicy => onSettings({ ...settings, encryptionPolicy })} />)}
    </dl>
    <ul className="space-y-1 text-sm">{items.map(item => <li key={item.key}>
      <span className="font-medium text-text-heading">{names[item.key] ?? autoJobName(item.database)}</span>
      {done(item) && !results[item.key]?.error && <span className="text-text-muted"> · {tx('Oluşturuldu')}</span>}
    </li>)}</ul>
    {failed.map(item => <ErrorNotice key={item.key} title={names[item.key] ?? autoJobName(item.database)} error={results[item.key].error} />)}
    <div className="flex flex-wrap items-center justify-between gap-3">
      <Button type="button" variant="outline" disabled={pending || items.some(done)} onClick={onBack}>{tx('Geri')}</Button>
      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" variant="outline" disabled={pending} onClick={() => onCreate(false)}>{tx('Sadece oluştur')}</Button>
        <Button type="button" size="lg" disabled={pending} onClick={() => onCreate(true)}>{pending ? tx('Oluşturuluyor…') : failed.length > 0 ? tx('Başarısızları yeniden dene') : tx('Yedeklemeyi oluştur ve ilk yedeği başlat')}</Button>
      </div>
    </div>
  </section>
}
