import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { BellOff } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { AddChannelDialog, ChannelSecretDialog, EditChannelDialog } from './channel-dialogs'
import { useClearChannelSecret, useDeleteChannel, useNotificationChannels, useSetChannelEnabled, useSetOwnerSecurityRecipient, useTestChannel } from './hooks'
import { authenticationLabels, kindLabels } from './labels'
import type { NotificationChannel } from './types'
import { formatDateTime, formatRelativeTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { TechnicalDetails } from '@/components/ui/technical-details'

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

  return <li><article aria-label={channel.name} className="space-y-3 rounded-xl border border-border p-4">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div className="min-w-0"><h3 className="break-words font-medium text-text-heading">{channel.name}</h3><p className="break-all text-sm text-text-muted">{kindLabels[channel.kind]} · {channel.targetDisplay}</p></div>
      <div className="flex flex-wrap gap-1"><StatusPill status={channel.enabled ? 'Active' : 'Disabled'} />{channel.kind === 'email' && <StatusPill level={channel.recipientVerified ? 'safe' : 'attention'}>{channel.recipientVerified ? tx('Adres onaylandı') : tx('Adres onayı bekleniyor')}</StatusPill>}</div>
    </div>
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
      <div><dt className="text-text-muted">{tx("Son gönderim")}</dt><dd>{last ? <span className="flex flex-wrap items-center gap-2"><StatusPill domain="delivery" status={last.status} /><time dateTime={last.at} title={formatDateTime(last.at)}>{formatRelativeTime(last.at)}</time></span> : tx('Henüz gönderim yok')}</dd></div>
    </dl>
    <div className="flex flex-wrap gap-2">
      <Button variant="outline" aria-label={label(tx('düzenle'))} onClick={() => { setDialog('edit') }}>{tx("Düzenle")}</Button>
      <Button variant="outline" aria-label={label(unconfirmed ? tx('onay isteğini yeniden gönder') : tx('test gönder'))} disabled={test.isPending} onClick={() => test.mutate(channel.id)}>{test.isPending ? tx('Gönderiliyor…') : unconfirmed ? tx('Onay isteğini yeniden gönder') : tx('Test gönder')}</Button>
    </div>
    <TechnicalDetails items={[
      { label: tx('Kanal kimliği'), value: channel.id },
      { label: tx('Oluşturma'), value: formatDateTime(channel.createdAt) },
      { label: tx('Son güncelleme'), value: formatDateTime(channel.updatedAt) },
      ...(last ? [{ label: tx('Son gönderim'), value: formatDateTime(last.at) }] : []),
      ...(last?.errorCode ? [{ label: tx('Hata kodu'), value: last.errorCode }] : []),
      ...(channel.secretUpdatedAt ? [{ label: tx('Gizli anahtar güncelleme zamanı'), value: formatDateTime(channel.secretUpdatedAt) }] : []),
      ...(channel.kind === 'webhook' ? [{ label: tx('Kimlik doğrulama'), value: authenticationLabels[channel.authentication] }] : []),
      ...(usesSecret ? [{ label: tx('Gizli anahtar'), value: channel.hasSecret ? tx('Secret kayıtlı') : tx('Secret ayarlanmadı') }] : []),
    ]}>
      {channel.kind === 'email' && <p className="text-sm text-text-muted">{channel.securityOwnerUserId ? tx('Bu adres bir tenant sahibinin zorunlu güvenlik bildirimlerini alır. Hedef adres değişirse kayıt kaldırılır.') : tx('Acil destek erişimi bildirimi için tenant sahibi doğrulanmış kendi e-posta adresini güvenlik alıcısı olarak kaydetmelidir. Genel bildirim politikası bu bildirimleri kapatmaz.')}</p>}
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" aria-label={label(channel.enabled ? tx('devre dışı bırak') : tx('etkinleştir'))} disabled={toggle.isPending} onClick={() => toggle.mutate({ id: channel.id, enabled: !channel.enabled })}>{channel.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>
        {owner && channel.kind === 'email' && (boundToMe || !channel.securityOwnerUserId) && <Button variant="outline"
        disabled={securityRecipient.isPending || (!boundToMe && (!channel.enabled || channel.recipientVerified !== true))}
        onClick={() => { securityRecipient.reset(); setDialog('ownerSecurity') }}>{boundToMe ? tx('Güvenlik alıcımı kaldır') : tx('Güvenlik alıcım olarak kaydet')}</Button>}
        {usesSecret && <Button variant="outline" aria-label={label(tx('secret ayarla'))} onClick={() => { setDialog('secret') }}>{channel.hasSecret ? tx('Secret yenile') : tx('Secret ayarla')}</Button>}
        {usesSecret && channel.hasSecret && <Button variant="outline" aria-label={label(tx('secret temizle'))} onClick={() => { clearSecret.reset(); setDialog('clearSecret') }}>{tx("Secret temizle")}</Button>}
        <Button variant="destructive" aria-label={label(tx('sil'))} onClick={() => { remove.reset(); setDialog('delete') }}>{tx("Sil")}</Button>
      </div>
    </TechnicalDetails>
    {toggle.error && <ErrorNotice error={toggle.error} />}
    {test.error && <ErrorNotice error={test.error} />}
    {test.isSuccess && <Notice tone="success">{unconfirmed ? tx('Onay isteği adrese gönderilmek üzere kuyruğa alındı; sonucu Gönderim geçmişi sekmesinde görün.') : tx('Test bildirimi kuyruğa alındı; sonucu Gönderim geçmişi sekmesinde görün.')}</Notice>}
    {dialog === 'edit' && <EditChannelDialog channel={channel} onClose={close} />}
    {dialog === 'secret' && <ChannelSecretDialog channel={channel} onClose={close} />}
    <ConfirmDialog open={dialog === 'clearSecret'} onClose={close} title={tx("“{0}” secret’ı temizlensin mi?", { "0": channel.name })} confirmLabel={tx("Secret’ı temizle")}
      consequence={{
        what: tx("Kayıtlı gizli değer silinir; yeniden ayarlanana kadar bu kanala yapılan gönderimler başarısız olur."),
        who: tx("Bu kanalı kullanan tüm bildirimler etkilenir."),
        reversible: tx("Evet. Yeni bir değer ayarlayarak yeniden çalıştırabilirsiniz; eski değer geri gelmez."),
      }}
      pending={clearSecret.isPending} error={clearSecret.error}
      onConfirm={() => clearSecret.mutate(channel.id, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'delete'} name={channel.name} onClose={close} title={tx("“{0}” silinsin mi?", { "0": channel.name })} confirmLabel={tx("Kanalı sil")}
      consequence={{
        what: tx("Kanal silinir ve bu kanala bildirim gönderilmez. Gönderim geçmişi korunur."),
        who: tx("Bu kanalı kullanan tüm bildirimler etkilenir."),
        reversible: tx("Hayır. Kanalı yeniden eklemeniz gerekir."),
      }}
      pending={remove.isPending} error={remove.error}
      onConfirm={() => remove.mutate(channel.id, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'ownerSecurity'} onClose={close}
      title={boundToMe ? tx('Güvenlik alıcısı kaldırılsın mı?') : tx('Bu adres güvenlik alıcınız olsun mu?')}
      consequence={boundToMe
        ? { what: tx('Bu adres artık zorunlu acil destek erişimi bildirimlerinizi almaz.'), who: tx('Yalnızca sizin bildirimleriniz etkilenir.'), reversible: tx('Evet. Aynı adresi yeniden güvenlik alıcısı yapabilirsiniz; önce başka bir doğrulanmış adres kaydedin.') }
        : { what: tx('Bu e-posta adresinin size ait olduğunu onaylıyorsunuz. Tenant sahibi olduğunuz sürece zorunlu acil destek erişimi bildirimleri, genel politika kapalı olsa da bu adrese gönderilir.'), who: tx('Yalnızca sizin bildirimleriniz etkilenir.'), reversible: tx('Evet. Daha sonra güvenlik alıcılığını kaldırabilirsiniz.') }}
      confirmLabel={boundToMe ? tx('Kaldır') : tx('Kaydet')} pending={securityRecipient.isPending}
      error={securityRecipient.error}
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
        ? <EmptyState icon={BellOff} message={tx("Henüz bildirim kanalı yok.")} action={addButton} />
        : <ul className="space-y-3">{channels.data.items.map((channel) => <ChannelCard key={channel.id} channel={channel} />)}</ul>)}
    </QueryState>
    {adding && <AddChannelDialog onClose={() => setAdding(false)} />}
  </Section>
}
