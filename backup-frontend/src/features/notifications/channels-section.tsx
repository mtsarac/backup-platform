import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { QueryState } from '@/components/data/query-state'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { errorMessage } from '@/lib/api/failure-messages'
import { AddChannelDialog, ChannelSecretDialog, EditChannelDialog } from './channel-dialogs'
import { useClearChannelSecret, useDeleteChannel, useNotificationChannels, useSetChannelEnabled, useTestChannel } from './hooks'
import { authenticationLabels, deliveryStatusLabels, kindLabels } from './labels'
import type { NotificationChannel } from './types'

type Dialog = 'edit' | 'secret' | 'clearSecret' | 'delete'

function ChannelCard({ channel }: { channel: NotificationChannel }) {
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const toggle = useSetChannelEnabled()
  const test = useTestChannel()
  const clearSecret = useClearChannelSecret()
  const remove = useDeleteChannel()
  const close = () => setDialog(null)
  const label = (text: string) => `${channel.name} kanalı: ${text}`
  const usesSecret = channel.kind === 'webhook' && channel.authentication !== 'none'
  const last = channel.lastDelivery

  return <li><article aria-label={channel.name} className="space-y-3 rounded-lg border border-border bg-surface-2 p-4">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div className="min-w-0"><h3 className="break-words font-medium text-text-heading">{channel.name}</h3><p className="break-all text-xs text-text-muted">{kindLabels[channel.kind]} · {channel.targetDisplay}</p></div>
      <Badge tone={channel.enabled ? 'success' : 'warning'}>{channel.enabled ? 'Etkin' : 'Devre dışı'}</Badge>
    </div>
    <dl className="grid gap-2 text-sm sm:grid-cols-2">
      {channel.kind === 'webhook' && <div><dt className="text-text-muted">Kimlik doğrulama</dt><dd>{authenticationLabels[channel.authentication]}{usesSecret && <> · {channel.hasSecret ? 'Secret kayıtlı' : <span className="text-warning">Secret ayarlanmadı</span>}</>}</dd></div>}
      <div><dt className="text-text-muted">Son gönderim</dt><dd>{last ? <>{deliveryStatusLabels[last.status].label} · {agentDate(last.at)}{last.errorCode && <> · <code>{last.errorCode}</code></>}</> : 'Henüz gönderim yok'}</dd></div>
    </dl>
    <div className="flex flex-wrap gap-1.5">
      <Button variant="outline" size="sm" aria-label={label('düzenle')} onClick={() => { setDialog('edit') }}>Düzenle</Button>
      <Button variant="outline" size="sm" aria-label={label(channel.enabled ? 'devre dışı bırak' : 'etkinleştir')} disabled={toggle.isPending} onClick={() => toggle.mutate({ id: channel.id, enabled: !channel.enabled })}>{channel.enabled ? 'Devre dışı bırak' : 'Etkinleştir'}</Button>
      <Button variant="outline" size="sm" aria-label={label('test gönder')} disabled={test.isPending} onClick={() => test.mutate(channel.id)}>{test.isPending ? 'Gönderiliyor…' : 'Test gönder'}</Button>
      {usesSecret && <Button variant="outline" size="sm" aria-label={label('secret ayarla')} onClick={() => { setDialog('secret') }}>{channel.hasSecret ? 'Secret yenile' : 'Secret ayarla'}</Button>}
      {usesSecret && channel.hasSecret && <Button variant="outline" size="sm" aria-label={label('secret temizle')} onClick={() => { clearSecret.reset(); setDialog('clearSecret') }}>Secret temizle</Button>}
      <Button variant="destructive" size="sm" aria-label={label('sil')} onClick={() => { remove.reset(); setDialog('delete') }}>Sil</Button>
    </div>
    {toggle.error && <p role="alert" className="text-xs text-error">{errorMessage(toggle.error)}</p>}
    {test.error && <p role="alert" className="text-xs text-error">{errorMessage(test.error)}</p>}
    {test.isSuccess && <p role="status" className="text-xs text-success">Test bildirimi kuyruğa alındı; sonucu aşağıdaki geçmişte görün.</p>}
    {dialog === 'edit' && <EditChannelDialog channel={channel} onClose={close} />}
    {dialog === 'secret' && <ChannelSecretDialog channel={channel} onClose={close} />}
    <ConfirmDialog open={dialog === 'clearSecret'} onClose={close} title={`“${channel.name}” secret’ı temizlensin mi?`} confirmLabel="Secret’ı temizle"
      description="Kayıtlı secret silinir; yeniden ayarlanana kadar webhook gönderimleri başarısız olur."
      pending={clearSecret.isPending} error={clearSecret.error ? errorMessage(clearSecret.error) : undefined}
      onConfirm={() => clearSecret.mutate(channel.id, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'delete'} onClose={close} title={`“${channel.name}” silinsin mi?`} confirmLabel="Kanalı sil"
      description="Kanal silinir. Gönderim geçmişi korunur."
      pending={remove.isPending} error={remove.error ? errorMessage(remove.error) : undefined}
      onConfirm={() => remove.mutate(channel.id, { onSuccess: close })} />
  </article></li>
}

export function ChannelsSection() {
  const channels = useNotificationChannels()
  const [adding, setAdding] = useState(false)
  return <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="notification-channels-heading">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div><h2 id="notification-channels-heading" className="font-medium text-text-heading">Kanallar</h2>
        <p className="mt-1 text-sm text-text-muted">Bildirimlerin gönderileceği adresler. Hedefler güvenlik için yalnızca kısaltılmış biçimde gösterilir.</p></div>
      <Button onClick={() => setAdding(true)}>Kanal ekle</Button>
    </div>
    <QueryState isPending={channels.isPending} error={channels.error} empty={channels.data?.items.length === 0}>
      <ul className="grid gap-3 lg:grid-cols-2">{channels.data?.items.map((channel) => <ChannelCard key={channel.id} channel={channel} />)}</ul>
    </QueryState>
    {adding && <AddChannelDialog onClose={() => setAdding(false)} />}
  </section>
}
