import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { Field } from '@/components/ui/field'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { ApiError } from '@/lib/api/client'
import { useCreateChannel, useSetChannelSecret, useUpdateChannel } from './hooks'
import { authenticationLabels, kindLabels } from './labels'
import { channelKinds, webhookAuthentications } from './types'
import type { ChannelKind, NotificationChannel, WebhookAuthentication } from './types'

const targetHints: Record<ChannelKind, { label: string; placeholder: string }> = {
  email: { get label() { return tx("E-posta adresi") }, placeholder: 'ops@example.com' },
  webhook: { get label() { return tx("Webhook adresi") }, placeholder: 'https://host/yol' },
  sms: { get label() { return tx("Telefon (E.164)") }, placeholder: '+905551112233' },
}
const invalidField = (error: unknown, field: string) => error instanceof ApiError && error.field === field

function AuthenticationSelect({ value, onChange }: { value: WebhookAuthentication; onChange: (value: WebhookAuthentication) => void }) {
  useLanguage()
  return <Field label={tx("Kimlik doğrulama")}>
    {(control) => <select {...control} className={`${selectClass} w-full`} value={value} onChange={(event) => onChange(event.target.value as WebhookAuthentication)}>{webhookAuthentications.map((item) => <option key={item} value={item}>{authenticationLabels[item]}</option>)}</select>}
  </Field>
}

/** Mounted only while open. The secret is typed once, sent, and cleared from state on submit. */
export function AddChannelDialog({ onClose }: { onClose: () => void }) {
  useLanguage()
  const create = useCreateChannel()
  const [kind, setKind] = useState<ChannelKind>('email')
  const [name, setName] = useState('')
  const [target, setTarget] = useState('')
  const [authentication, setAuthentication] = useState<WebhookAuthentication>('none')
  const [secret, setSecret] = useState('')
  const reset = create.reset
  useEffect(() => reset, [reset])
  const webhook = kind === 'webhook'
  const hint = targetHints[kind]

  function submit() {
    const input = { kind, name: name.trim(), target: target.trim(), ...(webhook ? { authentication, ...(authentication !== 'none' && secret ? { secret } : {}) } : {}) }
    setSecret('')
    create.mutate(input, { onSuccess: onClose })
  }

  return <FormDialog title={tx("Kanal ekle")} submitLabel="Kanal ekle" pendingLabel="Ekleniyor…" pending={create.isPending} disabled={!name.trim() || !target.trim()}
    error={create.error ? errorMessage(create.error) : undefined} onSubmit={submit} onClose={onClose}>
    <Field label={tx("Tür")}>
      {(control) => <select {...control} className={`${selectClass} w-full`} value={kind} onChange={(event) => setKind(event.target.value as ChannelKind)}>{channelKinds.map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</select>}
    </Field>
    <Field label={tx("Ad")}>
      {(control) => <input {...control} className={inputClass} value={name} maxLength={100} required aria-invalid={invalidField(create.error, 'name')} onChange={(event) => setName(event.target.value)} />}
    </Field>
    <Field label={hint.label} hint={kind === 'email' ? tx('Adrese bir onay e-postası gönderilir. Adres sahibi bağlantıya tıklayıp onaylayana kadar bildirim gönderilmez; her e-postada abonelikten çıkma bağlantısı bulunur.') : webhook ? tx('Webhook adresi yalnızca 443 numaralı HTTPS portunu kullanabilir (platform yöneticisi başka port tanımlamadıysa).') : undefined}>
      {(control) => <input {...control} className={inputClass} value={target} placeholder={hint.placeholder} required aria-invalid={invalidField(create.error, 'target')} onChange={(event) => setTarget(event.target.value)} />}
    </Field>
    {webhook && <AuthenticationSelect value={authentication} onChange={setAuthentication} />}
    {webhook && authentication !== 'none' && <Field label={authentication === 'bearer' ? tx('Bearer token') : tx('HMAC imza anahtarı')} hint={tx("Yalnızca bir kez girilir ve bir daha gösterilmez. Şimdi girmezseniz sonradan ayarlayın; ayarlanana kadar gönderimler başarısız olur.")}>
      {(control) => <input {...control} className={inputClass} type="password" autoComplete="new-password" value={secret} aria-invalid={invalidField(create.error, 'secret')} onChange={(event) => setSecret(event.target.value)} />}
    </Field>}
  </FormDialog>
}

export function EditChannelDialog({ channel, onClose }: { channel: NotificationChannel; onClose: () => void }) {
  useLanguage()
  const update = useUpdateChannel()
  const [name, setName] = useState(channel.name)
  const [target, setTarget] = useState('')
  const [authentication, setAuthentication] = useState(channel.authentication)
  const reset = update.reset
  useEffect(() => reset, [reset])
  const webhook = channel.kind === 'webhook'
  const hint = targetHints[channel.kind]

  function submit() {
    update.mutate({ id: channel.id, input: {
      ...(name.trim() !== channel.name ? { name: name.trim() } : {}), ...(target.trim() ? { target: target.trim() } : {}),
      ...(webhook && authentication !== channel.authentication ? { authentication } : {}),
    } }, { onSuccess: onClose })
  }

  return <FormDialog title={tx("Kanalı düzenle: {0}", { "0": channel.name })} submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={update.isPending} disabled={!name.trim()}
    description={webhook && authentication === 'none' && channel.authentication !== 'none' ? tx('Kimlik doğrulamayı kapatmak kayıtlı secret’ı siler.') : undefined}
    error={update.error ? errorMessage(update.error) : undefined} onSubmit={submit} onClose={onClose}>
    <Field label={tx("Ad")}>
      {(control) => <input {...control} className={inputClass} value={name} maxLength={100} required aria-invalid={invalidField(update.error, 'name')} onChange={(event) => setName(event.target.value)} />}
    </Field>
    <Field label={hint.label} hint={tx("Mevcut: {0} · boş bırakılırsa korunur", { "0": channel.targetDisplay })}>
      {(control) => <input {...control} className={inputClass} value={target} placeholder={hint.placeholder} aria-invalid={invalidField(update.error, 'target')} onChange={(event) => setTarget(event.target.value)} />}
    </Field>
    {webhook && <AuthenticationSelect value={authentication} onChange={setAuthentication} />}
    {webhook && authentication !== 'none' && authentication !== channel.authentication && <p className="text-sm text-text-muted">{tx("Kaydettikten sonra “Secret ayarla” ile secret girin; ayarlanana kadar gönderimler başarısız olur.")}</p>}
  </FormDialog>
}

export function ChannelSecretDialog({ channel, onClose }: { channel: NotificationChannel; onClose: () => void }) {
  useLanguage()
  const set = useSetChannelSecret()
  const [value, setValue] = useState('')
  const reset = set.reset
  useEffect(() => reset, [reset])

  function submit() {
    const next = value
    setValue('')
    set.mutate({ id: channel.id, value: next }, { onSuccess: onClose })
  }

  return <FormDialog title={tx("{0}: {1} ayarla", { "0": channel.name, "1": channel.authentication === 'bearer' ? tx('Bearer token') : tx('HMAC anahtarı') })} submitLabel={tx("Secret’ı kaydet")} pendingLabel={tx("Kaydediliyor…")} pending={set.isPending} disabled={!value}
    description={tx("Değer yalnızca gönderilir; bir daha gösterilmez.")} error={set.error ? errorMessage(set.error) : undefined} onSubmit={submit} onClose={onClose}>
    <Field label={tx("Yeni secret")}>
      {(control) => <input {...control} className={inputClass} type="password" autoComplete="new-password" value={value} aria-invalid={invalidField(set.error, 'value')} onChange={(event) => setValue(event.target.value)} />}
    </Field>
  </FormDialog>
}
