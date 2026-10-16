import { useState, type FormEvent } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { inputClass, selectClass } from '@/features/platform/styles'
import { errorMessage } from '@/lib/api/failure-messages'
import {
  useClearEmailSecret, useClearSmsSecret, useSetEmailSecret, useSetSmsSecret, useUpdateEmailTransport, useUpdateSmsTransport,
} from './hooks'
import type { EmailSecurity, EmailTransport, SmsAuthentication, SmsTransport } from './types'

const emailSecurityLabels: Record<EmailSecurity, string> = { starttls: 'STARTTLS', implicit: 'Implicit TLS', none: 'Şifreleme yok' }
const smsAuthLabels: Record<SmsAuthentication, string> = { none: 'Yok', bearer: 'Bearer token', basic: 'Basic (kullanıcı + parola)', header: 'Özel header' }

function StateBadges({ transport }: { transport: { configured: boolean; enabled: boolean } }) {
  return <div className="flex flex-wrap gap-1.5"><Badge tone={transport.configured ? 'success' : 'neutral'} dot>{transport.configured ? 'Yapılandırıldı' : 'Yapılandırılmadı'}</Badge><Badge tone={transport.enabled ? 'success' : 'warning'} dot>{transport.enabled ? 'Etkin' : 'Devre dışı'}</Badge></div>
}

/** One write-only secret control: the value is sent and cleared from state, never shown again; the API only reports `hasSecret`. */
function SecretControl({ label, transport, onSet, onClear }: {
  label: string
  transport: { hasSecret: boolean; secretUpdatedAt: string | null }
  onSet: { mutate: (value: string) => void; isPending: boolean; error: unknown; reset: () => void }
  onClear: { mutate: () => void; isPending: boolean; error: unknown; reset: () => void }
}) {
  const [value, setValue] = useState('')
  const [confirmClear, setConfirmClear] = useState(false)
  function submit(event: FormEvent) {
    event.preventDefault()
    const next = value
    setValue('')
    onClear.reset()
    onSet.mutate(next)
  }
  const error = onSet.error ?? onClear.error
  return <div className="space-y-3 border-t border-border pt-5">
    <h3 className="text-sm font-semibold text-text-heading">Kimlik bilgileri</h3>
    <p className="text-sm text-text-muted">{label}: {transport.hasSecret ? `kayıtlı (${agentDate(transport.secretUpdatedAt)})` : 'ayarlanmadı'}. Kayıtlı değer geri okunamaz.</p>
    <form onSubmit={submit} className="space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0 flex-1 basis-48"><Field label={`Yeni ${label.toLocaleLowerCase('tr-TR')}`}>{(control) => <input {...control} className={inputClass} type="password" autoComplete="new-password" value={value} onChange={(event) => setValue(event.target.value)} />}</Field></div>
        <Button type="submit" variant="outline" disabled={!value || onSet.isPending}>{onSet.isPending ? 'Kaydediliyor…' : 'Ayarla'}</Button>
        {transport.hasSecret && <Button type="button" variant="outline" disabled={onClear.isPending} onClick={() => { onSet.reset(); onClear.reset(); setConfirmClear(true) }}>Temizle</Button>}
      </div>
      {Boolean(error) && <Notice tone="error">{errorMessage(error)}</Notice>}
    </form>
    <ConfirmDialog open={confirmClear} onClose={() => setConfirmClear(false)} title={`${label} temizlensin mi?`} description="Kayıtlı kimlik bilgisi silinecek. Kimlik doğrulaması kullanan bildirim gönderimleri, yeni bir değer ayarlanana kadar başarısız olabilir." confirmLabel="Secret değerini temizle" pending={onClear.isPending} onConfirm={() => { onClear.mutate(); setConfirmClear(false) }} />
  </div>
}

const blank = (text: string) => text.trim() || null

export function EmailTransportCard({ transport }: { transport: EmailTransport }) {
  const save = useUpdateEmailTransport()
  const setSecret = useSetEmailSecret()
  const clearSecret = useClearEmailSecret()
  const [enabled, setEnabled] = useState(transport.enabled)
  const [host, setHost] = useState(transport.host ?? '')
  const [port, setPort] = useState(String(transport.port ?? 587))
  const [security, setSecurity] = useState<EmailSecurity>(transport.security)
  const [username, setUsername] = useState(transport.username ?? '')
  const [fromAddress, setFromAddress] = useState(transport.fromAddress ?? '')
  const [fromName, setFromName] = useState(transport.fromName ?? '')
  const portNumber = Number(port)
  const valid = host.trim() !== '' && fromAddress.trim() !== '' && Number.isInteger(portNumber) && portNumber >= 1 && portNumber <= 65535

  function submit(event: FormEvent) {
    event.preventDefault()
    if (valid) save.mutate({ enabled, host: host.trim(), port: portNumber, security, username: blank(username), fromAddress: fromAddress.trim(), fromName: blank(fromName) })
  }

  return <Section title="E-posta (SMTP)" description="Tenant e-posta bildirimleri bu SMTP sunucusu üzerinden gönderilir." actions={<StateBadges transport={transport} />}>
    <div className="space-y-6">
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />E-posta aktarımı etkin</label>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Sunucu">{(control) => <input {...control} className={inputClass} value={host} onChange={(event) => setHost(event.target.value)} />}</Field>
        <Field label="Port">{(control) => <input {...control} className={inputClass} inputMode="numeric" value={port} aria-invalid={!Number.isInteger(portNumber) || portNumber < 1 || portNumber > 65535} onChange={(event) => setPort(event.target.value)} />}</Field>
        <Field label="Güvenlik">{(control) => <select {...control} className={`${selectClass} w-full`} value={security} onChange={(event) => setSecurity(event.target.value as EmailSecurity)}>{(Object.keys(emailSecurityLabels) as EmailSecurity[]).map((item) => <option key={item} value={item}>{emailSecurityLabels[item]}</option>)}</select>}</Field>
        <Field label="Kullanıcı adı">{(control) => <input {...control} className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} />}</Field>
        <Field label="Gönderen adresi">{(control) => <input {...control} className={inputClass} value={fromAddress} onChange={(event) => setFromAddress(event.target.value)} />}</Field>
        <Field label="Gönderen adı">{(control) => <input {...control} className={inputClass} value={fromName} onChange={(event) => setFromName(event.target.value)} />}</Field>
      </div>
      <p className="text-xs text-text-muted">“Şifreleme yok” seçeneği kullanıcı adı/parola yapılandırıldığında reddedilir.</p>
      {save.isError && <Notice tone="error">{errorMessage(save.error)}</Notice>}
      {save.isSuccess && <Notice tone="success">E-posta aktarımı kaydedildi.</Notice>}
      <Button type="submit" disabled={save.isPending || !valid}>{save.isPending ? 'Kaydediliyor…' : 'E-posta ayarlarını kaydet'}</Button>
    </form>
    <SecretControl label="SMTP parolası" transport={transport} onSet={setSecret} onClear={clearSecret} />
    </div>
  </Section>
}

export function SmsTransportCard({ transport }: { transport: SmsTransport }) {
  const save = useUpdateSmsTransport()
  const setSecret = useSetSmsSecret()
  const clearSecret = useClearSmsSecret()
  const [enabled, setEnabled] = useState(transport.enabled)
  const [endpoint, setEndpoint] = useState(transport.endpoint ?? '')
  const [authentication, setAuthentication] = useState<SmsAuthentication>(transport.authentication)
  const [username, setUsername] = useState(transport.username ?? '')
  const [headerName, setHeaderName] = useState(transport.headerName ?? '')
  const [sender, setSender] = useState(transport.sender ?? '')

  function submit(event: FormEvent) {
    event.preventDefault()
    if (!endpoint.trim()) return
    save.mutate({
      enabled, provider: 'generic-http', endpoint: endpoint.trim(), authentication,
      username: authentication === 'basic' ? blank(username) : null, headerName: authentication === 'header' ? blank(headerName) : null, sender: blank(sender),
    })
  }

  return <Section title="SMS ağ geçidi" description="Tenant SMS bildirimlerinin platform genelindeki gönderim ayarları." actions={<StateBadges transport={transport} />}>
    <div className="space-y-6">
    <Notice tone="warning" title="UYGULANDI — DOĞRULANMADI">Yalnızca genel bir HTTP JSON bağdaştırıcısı uygulandı; hiçbir gerçek SMS sağlayıcısına karşı doğrulanmadı. Canlıya almadan önce kendi sağlayıcınızla deneyin.</Notice>
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />SMS aktarımı etkin</label>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Sağlayıcı">{(control) => <input {...control} className={inputClass} value="generic-http" disabled readOnly />}</Field>
        <Field label="Uç nokta (https://…)">{(control) => <input {...control} className={inputClass} value={endpoint} onChange={(event) => setEndpoint(event.target.value)} />}</Field>
        <Field label="Kimlik doğrulama">{(control) => <select {...control} className={`${selectClass} w-full`} value={authentication} onChange={(event) => setAuthentication(event.target.value as SmsAuthentication)}>{(Object.keys(smsAuthLabels) as SmsAuthentication[]).map((item) => <option key={item} value={item}>{smsAuthLabels[item]}</option>)}</select>}</Field>
        <Field label="Gönderen">{(control) => <input {...control} className={inputClass} value={sender} onChange={(event) => setSender(event.target.value)} />}</Field>
        {authentication === 'basic' && <Field label="Kullanıcı adı">{(control) => <input {...control} className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} />}</Field>}
        {authentication === 'header' && <Field label="Header adı">{(control) => <input {...control} className={inputClass} value={headerName} onChange={(event) => setHeaderName(event.target.value)} />}</Field>}
      </div>
      {save.isError && <Notice tone="error">{errorMessage(save.error)}</Notice>}
      {save.isSuccess && <Notice tone="success">SMS aktarımı kaydedildi.</Notice>}
      <Button type="submit" disabled={save.isPending || !endpoint.trim()}>{save.isPending ? 'Kaydediliyor…' : 'SMS ayarlarını kaydet'}</Button>
    </form>
    {authentication !== 'none' && <SecretControl label={authentication === 'bearer' ? 'Bearer token' : authentication === 'basic' ? 'Parola' : 'Header değeri'} transport={transport} onSet={setSecret} onClear={clearSecret} />}
    </div>
  </Section>
}
