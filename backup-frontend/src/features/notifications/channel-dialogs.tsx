import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { Field } from '@/components/ui/field'
import { SecretField } from '@/components/ui/secret-field'
import { useSecretValue } from '@/components/ui/secret-value'
import { FormDialog } from '@/components/ui/form-dialog'
import { TextInput } from '@/components/ui/text-input'
import { ApiError } from '@/lib/api/client'
import { useCreateChannel, useSetChannelSecret, useUpdateChannel } from './hooks'
import { kindLabels } from './labels'
import { visibleChannelKinds } from './types'
import type { ChannelKind, NotificationChannel } from './types'
import { Select } from '@/components/ui/text-input'

const targetHints: Record<ChannelKind, { label: string; placeholder: string }> = {
  email: { get label() { return tx("E-posta adresi") }, placeholder: 'ops@example.com' },
  webhook: { get label() { return tx("Webhook adresi") }, get placeholder() { return tx('https://host/yol') } },
  sms: { get label() { return tx("Telefon (E.164)") }, placeholder: '+905551112233' },
}
const invalidField = (error: unknown, field: string) => error instanceof ApiError && error.field === field


/** Mounted only while open. The secret is typed once, sent, and cleared from state on submit. */
export function AddChannelDialog({ onClose }: { onClose: () => void }) {
  useLanguage()
  const create = useCreateChannel()
  const [kind, setKind] = useState<ChannelKind>('email')
  const [name, setName] = useState('')
  const [target, setTarget] = useState('')
  const secret = useSecretValue()
  const [error, setError] = useState<unknown>(null)
  const reset = create.reset
  useEffect(() => reset, [reset])
  const webhook = kind === 'webhook'
  const hint = targetHints[kind]

  function submit() {
    setError(null)
    const taken = secret.take()
    const input = { kind, name: name.trim(), target: target.trim(), ...(webhook ? { authentication: taken ? 'hmac' as const : 'none' as const, ...(taken ? { secret: taken } : {}) } : {}) }
    create.mutate(input, {
      onSuccess: onClose,
      onError: error => { setError(error); create.reset() },
    })
  }

  return <FormDialog title={tx("Kanal ekle")} submitLabel={tx("Kanal ekle")} pendingLabel={tx("Ekleniyor…")} pending={create.isPending} disabled={!name.trim() || !target.trim()}
    error={error} onSubmit={submit} onClose={onClose}>
    <Field label={tx("Tür")}>
      {(control) => <Select {...control} value={kind} onChange={(event) => setKind(event.target.value as ChannelKind)}>{visibleChannelKinds().map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</Select>}
    </Field>
    <p className="text-sm text-text-muted">{webhook
      ? tx('Olay, önem düzeyi ve etkilenen yedek veya bilgisayar bilgileri bu HTTPS adresine JSON olarak gönderilir. Yedek dosyalarının içeriği gönderilmez.')
      : kind === 'email'
        ? tx('Olay ve önem bilgileri, etkilenen yedek veya bilgisayarın adıyla birlikte bu e-posta adresine gönderilir. Yedek dosyalarının içeriği gönderilmez.')
        : tx('Uyarıların kısa özeti bu telefon numarasına SMS olarak gönderilir.')}</p>
    <Field label={tx("Ad")}>
      {(control) => <TextInput {...control} value={name} maxLength={100} required aria-invalid={invalidField(error, 'name')} onChange={(event) => setName(event.target.value)} />}
    </Field>
    <Field label={hint.label} hint={kind === 'email' ? tx('Adrese bir onay e-postası gönderilir. Adres sahibi bağlantıya tıklayıp onaylayana kadar bildirim gönderilmez; her e-postada abonelikten çıkma bağlantısı bulunur.') : webhook ? tx('Webhook adresi yalnızca 443 numaralı HTTPS portunu kullanabilir (platform yöneticisi başka port tanımlamadıysa).') : undefined}>
      {(control) => <TextInput {...control} value={target} placeholder={hint.placeholder} required aria-invalid={invalidField(error, 'target')} onChange={(event) => setTarget(event.target.value)} />}
    </Field>
    {webhook && <SecretField label={tx('Gizli anahtar')} value={secret.value} invalid={invalidField(error, 'secret')} onChange={secret.set} />}
  </FormDialog>
}

export function EditChannelDialog({ channel, onClose }: { channel: NotificationChannel; onClose: () => void }) {
  useLanguage()
  const update = useUpdateChannel()
  const [name, setName] = useState(channel.name)
  const [target, setTarget] = useState('')
  const secret = useSecretValue()
  const setSecret = useSetChannelSecret()
  const reset = update.reset
  useEffect(() => reset, [reset])
  const webhook = channel.kind === 'webhook'
  const hint = targetHints[channel.kind]

  function submit() {
    const value = secret.take()
    update.mutate({ id: channel.id, input: {
      ...(name.trim() !== channel.name ? { name: name.trim() } : {}), ...(target.trim() ? { target: target.trim() } : {}),
      ...(webhook && value && channel.authentication === 'none' ? { authentication: 'hmac' as const } : {}),
    } }, { onSuccess: () => value ? setSecret.mutate({ id: channel.id, value }, { onSuccess: onClose }) : onClose() })
  }

  return <FormDialog title={tx("Kanalı düzenle: {0}", { "0": channel.name })} submitLabel={tx("Kaydet")} pendingLabel={tx("Kaydediliyor…")} pending={update.isPending || setSecret.isPending} disabled={!name.trim()}
    error={update.error || setSecret.error} onSubmit={submit} onClose={onClose}>
    <Field label={tx("Ad")}>
      {(control) => <TextInput {...control} value={name} maxLength={100} required aria-invalid={invalidField(update.error, 'name')} onChange={(event) => setName(event.target.value)} />}
    </Field>
    <Field label={hint.label} hint={tx("Mevcut: {0} · boş bırakılırsa korunur", { "0": channel.targetDisplay })}>
      {(control) => <TextInput {...control} value={target} placeholder={hint.placeholder} aria-invalid={invalidField(update.error, 'target')} onChange={(event) => setTarget(event.target.value)} />}
    </Field>
    {webhook && <SecretField label={tx('Gizli anahtar')} value={secret.value} invalid={invalidField(setSecret.error, 'value')} onChange={secret.set} />}
  </FormDialog>
}

