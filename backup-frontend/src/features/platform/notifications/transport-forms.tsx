import { useState, type FormEvent, type ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
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
  return <div className="flex gap-1.5"><Badge tone={transport.configured ? 'success' : 'neutral'}>{transport.configured ? 'Yapılandırıldı' : 'Yapılandırılmadı'}</Badge><Badge tone={transport.enabled ? 'success' : 'warning'}>{transport.enabled ? 'Etkin' : 'Devre dışı'}</Badge></div>
}

/** One write-only secret control: the value is sent and cleared from state, never shown again; the API only reports `hasSecret`. */
function SecretControl({ label, transport, onSet, onClear }: {
  label: string
  transport: { hasSecret: boolean; secretUpdatedAt: string | null }
  onSet: { mutate: (value: string) => void; isPending: boolean; error: unknown; reset: () => void }
  onClear: { mutate: () => void; isPending: boolean; error: unknown; reset: () => void }
}) {
  const [value, setValue] = useState('')
  function submit(event: FormEvent) {
    event.preventDefault()
    const next = value
    setValue('')
    onClear.reset()
    onSet.mutate(next)
  }
  const error = onSet.error ?? onClear.error
  return <form onSubmit={submit} className="space-y-2 border-t border-border pt-3">
    <p className="text-sm text-text-muted">{label}: {transport.hasSecret ? `kayıtlı (${agentDate(transport.secretUpdatedAt)})` : 'ayarlanmadı'}</p>
    <div className="flex flex-wrap items-end gap-2">
      <label className="min-w-48 flex-1 text-sm text-text-muted">Yeni {label.toLocaleLowerCase('tr-TR')}<input className={inputClass} type="password" autoComplete="new-password" value={value} onChange={(event) => setValue(event.target.value)} /></label>
      <Button type="submit" variant="outline" disabled={!value || onSet.isPending}>{onSet.isPending ? 'Kaydediliyor…' : 'Ayarla'}</Button>
      {transport.hasSecret && <Button type="button" variant="outline" disabled={onClear.isPending} onClick={() => { onSet.reset(); onClear.mutate() }}>Temizle</Button>}
    </div>
    {error ? <p role="alert" className="text-sm text-error">{errorMessage(error)}</p> : null}
  </form>
}

function Card({ id, title, description, badges, children }: { id: string; title: string; description: ReactNode; badges: ReactNode; children: ReactNode }) {
  return <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby={id}>
    <div className="flex flex-wrap items-start justify-between gap-2"><div><h2 id={id} className="font-medium text-text-heading">{title}</h2><div className="mt-1 text-sm text-text-muted">{description}</div></div>{badges}</div>
    {children}
  </section>
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

  return <Card id="transport-email-heading" title="E-posta (SMTP)" description="Tenant e-posta bildirimleri bu SMTP sunucusu üzerinden gönderilir." badges={<StateBadges transport={transport} />}>
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />E-posta aktarımı etkin</label>
      <div className="grid gap-4 sm:grid-cols-2">
        <label className="block text-sm text-text-muted">Sunucu<input className={inputClass} value={host} onChange={(event) => setHost(event.target.value)} /></label>
        <label className="block text-sm text-text-muted">Port<input className={inputClass} inputMode="numeric" value={port} aria-invalid={!Number.isInteger(portNumber) || portNumber < 1 || portNumber > 65535} onChange={(event) => setPort(event.target.value)} /></label>
        <label className="block text-sm text-text-muted">Güvenlik<select className={selectClass} value={security} onChange={(event) => setSecurity(event.target.value as EmailSecurity)}>{(Object.keys(emailSecurityLabels) as EmailSecurity[]).map((item) => <option key={item} value={item}>{emailSecurityLabels[item]}</option>)}</select></label>
        <label className="block text-sm text-text-muted">Kullanıcı adı<input className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} /></label>
        <label className="block text-sm text-text-muted">Gönderen adresi<input className={inputClass} value={fromAddress} onChange={(event) => setFromAddress(event.target.value)} /></label>
        <label className="block text-sm text-text-muted">Gönderen adı<input className={inputClass} value={fromName} onChange={(event) => setFromName(event.target.value)} /></label>
      </div>
      <p className="text-xs text-text-muted">“Şifreleme yok” seçeneği kullanıcı adı/parola yapılandırıldığında reddedilir.</p>
      {save.isError && <p role="alert" className="text-sm text-error">{errorMessage(save.error)}</p>}
      {save.isSuccess && <p role="status" className="text-sm text-success">E-posta aktarımı kaydedildi.</p>}
      <Button type="submit" disabled={save.isPending || !valid}>{save.isPending ? 'Kaydediliyor…' : 'E-posta ayarlarını kaydet'}</Button>
    </form>
    <SecretControl label="SMTP parolası" transport={transport} onSet={setSecret} onClear={clearSecret} />
  </Card>
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

  return <Card id="transport-sms-heading" title="SMS ağ geçidi" badges={<StateBadges transport={transport} />}
    description={<><Badge tone="warning">UYGULANDI — DOĞRULANMADI</Badge> <span className="mt-1 block">Yalnızca genel bir HTTP JSON bağdaştırıcısı uygulandı; hiçbir gerçek SMS sağlayıcısına karşı doğrulanmadı. Canlıya almadan önce kendi sağlayıcınızla deneyin.</span></>}>
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />SMS aktarımı etkin</label>
      <div className="grid gap-4 sm:grid-cols-2">
        <label className="block text-sm text-text-muted">Sağlayıcı<input className={inputClass} value="generic-http" disabled readOnly /></label>
        <label className="block text-sm text-text-muted">Uç nokta (https://…)<input className={inputClass} value={endpoint} onChange={(event) => setEndpoint(event.target.value)} /></label>
        <label className="block text-sm text-text-muted">Kimlik doğrulama<select className={selectClass} value={authentication} onChange={(event) => setAuthentication(event.target.value as SmsAuthentication)}>{(Object.keys(smsAuthLabels) as SmsAuthentication[]).map((item) => <option key={item} value={item}>{smsAuthLabels[item]}</option>)}</select></label>
        <label className="block text-sm text-text-muted">Gönderen<input className={inputClass} value={sender} onChange={(event) => setSender(event.target.value)} /></label>
        {authentication === 'basic' && <label className="block text-sm text-text-muted">Kullanıcı adı<input className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} /></label>}
        {authentication === 'header' && <label className="block text-sm text-text-muted">Header adı<input className={inputClass} value={headerName} onChange={(event) => setHeaderName(event.target.value)} /></label>}
      </div>
      {save.isError && <p role="alert" className="text-sm text-error">{errorMessage(save.error)}</p>}
      {save.isSuccess && <p role="status" className="text-sm text-success">SMS aktarımı kaydedildi.</p>}
      <Button type="submit" disabled={save.isPending || !endpoint.trim()}>{save.isPending ? 'Kaydediliyor…' : 'SMS ayarlarını kaydet'}</Button>
    </form>
    {authentication !== 'none' && <SecretControl label={authentication === 'bearer' ? 'Bearer token' : authentication === 'basic' ? 'Parola' : 'Header değeri'} transport={transport} onSet={setSecret} onClear={clearSecret} />}
  </Card>
}
