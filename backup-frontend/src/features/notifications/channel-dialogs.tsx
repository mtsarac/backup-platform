import { useEffect, useState } from 'react'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass, selectClass } from '@/features/platform/styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { ApiError } from '@/lib/api/client'
import { useCreateChannel, useSetChannelSecret, useUpdateChannel } from './hooks'
import { authenticationLabels, kindLabels } from './labels'
import { channelKinds, webhookAuthentications, type ChannelKind, type NotificationChannel, type WebhookAuthentication } from './types'

const targetHints: Record<ChannelKind, { label: string; placeholder: string }> = {
  email: { label: 'E-posta adresi', placeholder: 'ops@example.com' },
  webhook: { label: 'Webhook adresi', placeholder: 'https://host/yol' },
  sms: { label: 'Telefon (E.164)', placeholder: '+905551112233' },
}
const invalidField = (error: unknown, field: string) => error instanceof ApiError && error.field === field

function AuthenticationSelect({ value, onChange }: { value: WebhookAuthentication; onChange: (value: WebhookAuthentication) => void }) {
  return <label className="block text-sm text-text-muted">Kimlik doğrulama
    <select className={selectClass} value={value} onChange={(event) => onChange(event.target.value as WebhookAuthentication)}>{webhookAuthentications.map((item) => <option key={item} value={item}>{authenticationLabels[item]}</option>)}</select>
  </label>
}

/** Mounted only while open. The secret is typed once, sent, and cleared from state on submit. */
export function AddChannelDialog({ onClose }: { onClose: () => void }) {
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

  return <FormDialog title="Kanal ekle" submitLabel="Kanal ekle" pendingLabel="Ekleniyor…" pending={create.isPending} disabled={!name.trim() || !target.trim()}
    error={create.error ? errorMessage(create.error) : undefined} onSubmit={submit} onClose={onClose}>
    <label className="block text-sm text-text-muted">Tür
      <select className={selectClass} value={kind} onChange={(event) => setKind(event.target.value as ChannelKind)}>{channelKinds.map((item) => <option key={item} value={item}>{kindLabels[item]}</option>)}</select>
    </label>
    <label className="block text-sm text-text-muted">Ad
      <input className={inputClass} value={name} maxLength={100} required aria-invalid={invalidField(create.error, 'name')} onChange={(event) => setName(event.target.value)} />
    </label>
    <label className="block text-sm text-text-muted">{hint.label}
      <input className={inputClass} value={target} placeholder={hint.placeholder} required aria-invalid={invalidField(create.error, 'target')} onChange={(event) => setTarget(event.target.value)} />
    </label>
    {kind === 'email' && <p className="text-xs text-text-muted">Adrese bir onay e-postası gönderilir. Adres sahibi bağlantıya tıklayıp onaylayana kadar bildirim gönderilmez; her e-postada abonelikten çıkma bağlantısı bulunur.</p>}
    {webhook && <p className="text-xs text-text-muted">Webhook adresi yalnızca 443 numaralı HTTPS portunu kullanabilir (platform yöneticisi başka port tanımlamadıysa).</p>}
    {webhook && <AuthenticationSelect value={authentication} onChange={setAuthentication} />}
    {webhook && authentication !== 'none' && <label className="block text-sm text-text-muted">{authentication === 'bearer' ? 'Bearer token' : 'HMAC imza anahtarı'}
      <input className={inputClass} type="password" autoComplete="new-password" value={secret} aria-invalid={invalidField(create.error, 'secret')} onChange={(event) => setSecret(event.target.value)} />
      <span className="text-xs">Yalnızca bir kez girilir ve bir daha gösterilmez. Şimdi girmezseniz sonradan ayarlayın; ayarlanana kadar gönderimler başarısız olur.</span>
    </label>}
  </FormDialog>
}

export function EditChannelDialog({ channel, onClose }: { channel: NotificationChannel; onClose: () => void }) {
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

  return <FormDialog title={`Kanalı düzenle: ${channel.name}`} submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={update.isPending} disabled={!name.trim()}
    description={webhook && authentication === 'none' && channel.authentication !== 'none' ? 'Kimlik doğrulamayı kapatmak kayıtlı secret’ı siler.' : undefined}
    error={update.error ? errorMessage(update.error) : undefined} onSubmit={submit} onClose={onClose}>
    <label className="block text-sm text-text-muted">Ad
      <input className={inputClass} value={name} maxLength={100} required aria-invalid={invalidField(update.error, 'name')} onChange={(event) => setName(event.target.value)} />
    </label>
    <label className="block text-sm text-text-muted">{hint.label}
      <input className={inputClass} value={target} placeholder={`Mevcut: ${channel.targetDisplay} · boş bırakılırsa korunur`} aria-invalid={invalidField(update.error, 'target')} onChange={(event) => setTarget(event.target.value)} />
    </label>
    {webhook && <AuthenticationSelect value={authentication} onChange={setAuthentication} />}
    {webhook && authentication !== 'none' && authentication !== channel.authentication && <p className="text-xs text-text-muted">Kaydettikten sonra “Secret ayarla” ile secret girin; ayarlanana kadar gönderimler başarısız olur.</p>}
  </FormDialog>
}

export function ChannelSecretDialog({ channel, onClose }: { channel: NotificationChannel; onClose: () => void }) {
  const set = useSetChannelSecret()
  const [value, setValue] = useState('')
  const reset = set.reset
  useEffect(() => reset, [reset])

  function submit() {
    const next = value
    setValue('')
    set.mutate({ id: channel.id, value: next }, { onSuccess: onClose })
  }

  return <FormDialog title={`${channel.name}: ${channel.authentication === 'bearer' ? 'Bearer token' : 'HMAC anahtarı'} ayarla`} submitLabel="Secret’ı kaydet" pendingLabel="Kaydediliyor…" pending={set.isPending} disabled={!value}
    description="Değer yalnızca gönderilir; bir daha gösterilmez." error={set.error ? errorMessage(set.error) : undefined} onSubmit={submit} onClose={onClose}>
    <label className="block text-sm text-text-muted">Yeni secret
      <input className={inputClass} type="password" autoComplete="new-password" value={value} aria-invalid={invalidField(set.error, 'value')} onChange={(event) => setValue(event.target.value)} />
    </label>
  </FormDialog>
}
