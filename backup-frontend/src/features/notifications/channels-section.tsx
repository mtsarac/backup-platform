import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { BellOff } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { errorMessage } from '@/lib/api/failure-messages'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { AddChannelDialog, ChannelSecretDialog, EditChannelDialog } from './channel-dialogs'
import { useClearChannelSecret, useDeleteChannel, useNotificationChannels, useSetChannelEnabled, useSetOwnerSecurityRecipient, useTestChannel } from './hooks'
import { authenticationLabels, deliveryStatusLabels, kindLabels } from './labels'
import type { NotificationChannel } from './types'

type Dialog = 'edit' | 'secret' | 'clearSecret' | 'delete' | 'ownerSecurity'

function ChannelCard({ channel }: { channel: NotificationChannel }) {
  useLanguage()
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const toggle = useSetChannelEnabled()
  const test = useTestChannel()
  const clearSecret = useClearChannelSecret()
  const remove = useDeleteChannel()
  const securityRecipient = useSetOwnerSecurityRecipient()
  const { user } = useAuth()
  const { tenant } = useTenant()
  const owner = tenant.role === 'Owner' || tenant.membershipRole === 'Owner'
  const boundToMe = Boolean(user && channel.securityOwnerUserId === user.userId)
  const close = () => setDialog(null)
  const label = (text: string) => tx("{0} kanalı: {1}", { "0": channel.name, "1": text })
  const usesSecret = channel.kind === 'webhook' && channel.authentication !== 'none'
  const last = channel.lastDelivery
  const unconfirmed = channel.kind === 'email' && channel.recipientVerified === false

  return <li className="py-4 first:pt-0 last:pb-0"><article aria-label={channel.name} className="space-y-3">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div className="min-w-0"><h3 className="break-words font-medium text-text-heading">{channel.name}</h3><p className="break-all text-sm text-text-muted">{kindLabels[channel.kind]} · {channel.targetDisplay}</p></div>
      <div className="flex flex-wrap gap-1"><Badge dot tone={channel.enabled ? 'success' : 'warning'}>{channel.enabled ? tx('Etkin') : tx('Devre dışı')}</Badge>{unconfirmed && <Badge tone="warning">{tx("Adres onayı bekleniyor")}</Badge>}</div>
    </div>
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
      {channel.kind === 'webhook' && <div><dt className="text-text-muted">{tx("Kimlik doğrulama")}</dt><dd>{authenticationLabels[channel.authentication]}{usesSecret && <> · {channel.hasSecret ? tx('Secret kayıtlı') : <span className="text-warning">{tx("Secret ayarlanmadı")}</span>}</>}</dd></div>}
      <div><dt className="text-text-muted">{tx("Son gönderim")}</dt><dd>{last ? <>{tx(deliveryStatusLabels[last.status].label)} · {agentDate(last.at)}{last.errorCode && <> · <code>{last.errorCode}</code></>}</> : tx('Henüz gönderim yok')}</dd></div>
    </dl>
    {channel.kind === 'email' && <p className="text-sm text-text-muted">{channel.securityOwnerUserId ? tx('Bu adres bir tenant sahibinin zorunlu güvenlik bildirimlerini alır. Hedef adres değişirse kayıt kaldırılır.') : tx('Break-glass bildirimi için tenant sahibi doğrulanmış kendi e-posta adresini güvenlik alıcısı olarak kaydetmelidir. Genel bildirim politikası bu bildirimleri kapatmaz.')}</p>}
    <div className="flex flex-wrap gap-2">
      <Button variant="outline" size="sm" aria-label={label(tx('düzenle'))} onClick={() => { setDialog('edit') }}>{tx("Düzenle")}</Button>
      <Button variant="outline" size="sm" aria-label={label(channel.enabled ? tx('devre dışı bırak') : tx('etkinleştir'))} disabled={toggle.isPending} onClick={() => toggle.mutate({ id: channel.id, enabled: !channel.enabled })}>{channel.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>
      <Button variant="outline" size="sm" aria-label={label(unconfirmed ? tx('onay isteğini yeniden gönder') : tx('test gönder'))} disabled={test.isPending} onClick={() => test.mutate(channel.id)}>{test.isPending ? tx('Gönderiliyor…') : unconfirmed ? tx('Onay isteğini yeniden gönder') : tx('Test gönder')}</Button>
      {owner && channel.kind === 'email' && (boundToMe || !channel.securityOwnerUserId) && <Button variant="outline" size="sm"
        disabled={securityRecipient.isPending || (!boundToMe && (!channel.enabled || channel.recipientVerified !== true))}
        onClick={() => { securityRecipient.reset(); setDialog('ownerSecurity') }}>{boundToMe ? tx('Güvenlik alıcımı kaldır') : tx('Güvenlik alıcım olarak kaydet')}</Button>}
      {usesSecret && <Button variant="outline" size="sm" aria-label={label(tx('secret ayarla'))} onClick={() => { setDialog('secret') }}>{channel.hasSecret ? tx('Secret yenile') : tx('Secret ayarla')}</Button>}
      {usesSecret && channel.hasSecret && <Button variant="outline" size="sm" aria-label={label(tx('secret temizle'))} onClick={() => { clearSecret.reset(); setDialog('clearSecret') }}>{tx("Secret temizle")}</Button>}
      <Button variant="destructive" size="sm" aria-label={label('sil')} onClick={() => { remove.reset(); setDialog('delete') }}>{tx("Sil")}</Button>
    </div>
    {toggle.error && <Notice tone="error">{errorMessage(toggle.error)}</Notice>}
    {test.error && <Notice tone="error">{errorMessage(test.error)}</Notice>}
    {test.isSuccess && <Notice tone="success">{unconfirmed ? tx('Onay isteği adrese gönderilmek üzere kuyruğa alındı; sonucu Gönderim geçmişi sekmesinde görün.') : tx('Test bildirimi kuyruğa alındı; sonucu Gönderim geçmişi sekmesinde görün.')}</Notice>}
    {dialog === 'edit' && <EditChannelDialog channel={channel} onClose={close} />}
    {dialog === 'secret' && <ChannelSecretDialog channel={channel} onClose={close} />}
    <ConfirmDialog open={dialog === 'clearSecret'} onClose={close} title={tx("“{0}” secret’ı temizlensin mi?", { "0": channel.name })} confirmLabel={tx("Secret’ı temizle")}
      description={tx("Kayıtlı secret silinir; yeniden ayarlanana kadar webhook gönderimleri başarısız olur.")}
      pending={clearSecret.isPending} error={clearSecret.error ? errorMessage(clearSecret.error) : undefined}
      onConfirm={() => clearSecret.mutate(channel.id, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'delete'} onClose={close} title={tx("“{0}” silinsin mi?", { "0": channel.name })} confirmLabel="Kanalı sil"
      description={tx("Kanal silinir. Gönderim geçmişi korunur.")}
      pending={remove.isPending} error={remove.error ? errorMessage(remove.error) : undefined}
      onConfirm={() => remove.mutate(channel.id, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'ownerSecurity'} onClose={close}
      title={boundToMe ? tx('Güvenlik alıcısı kaldırılsın mı?') : tx('Bu adres güvenlik alıcınız olsun mu?')}
      description={boundToMe ? tx('Bu adres artık sizin zorunlu break-glass bildirimlerinizi almaz. Başka bir doğrulanmış adres kaydedin.') : tx('Bu e-posta adresinin size ait olduğunu onaylıyorsunuz. Tenant sahibi olduğunuz sürece zorunlu break-glass bildirimleri genel politika kapalı olsa da bu adrese gönderilir.')}
      confirmLabel={boundToMe ? tx('Kaldır') : tx('Kaydet')} pending={securityRecipient.isPending}
      error={securityRecipient.error ? errorMessage(securityRecipient.error) : undefined}
      onConfirm={() => securityRecipient.mutate({ id: channel.id, enabled: !boundToMe }, { onSuccess: close })} />
  </article></li>
}

export function ChannelsSection() {
  useLanguage()
  const channels = useNotificationChannels()
  const [adding, setAdding] = useState(false)
  const addButton = <Button onClick={() => setAdding(true)}>{tx("Kanal ekle")}</Button>
  return <Section title={tx("Kanallar")} description={tx("Bildirimlerin gönderileceği adresler. Hedefler güvenlik için yalnızca kısaltılmış biçimde gösterilir.")} actions={channels.data?.items.length ? addButton : undefined}>
    <QueryState isPending={channels.isPending} error={channels.error}>
      {channels.data && (channels.data.items.length === 0
        ? <EmptyState icon={BellOff} title={tx("Henüz bildirim kanalı yok.")} action={addButton}>{tx("E-posta, webhook veya SMS kanalı ekleyerek uyarıları almaya başlayın.")}</EmptyState>
        : <ul className="divide-y divide-border">{channels.data.items.map((channel) => <ChannelCard key={channel.id} channel={channel} />)}</ul>)}
    </QueryState>
    {adding && <AddChannelDialog onClose={() => setAdding(false)} />}
  </Section>
}
