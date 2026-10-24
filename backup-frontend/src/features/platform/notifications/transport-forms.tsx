import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import {
  useClearEmailSecret, useClearSmsSecret, useSetEmailSecret, useSetSmsSecret, useUpdateEmailTransport, useUpdateSmsTransport,
} from './hooks'
import type { EmailSecurity, EmailTransport, SmsAuthentication, SmsTransport } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

const emailSecurityLabels: Record<EmailSecurity, string> = { starttls: 'STARTTLS', get implicit() { return tx("Implicit TLS") }, get none() { return tx("Şifreleme yok") } }
const smsAuthLabels: Record<SmsAuthentication, string> = { get none() { return tx("Yok") }, get bearer() { return tx("Bearer token") }, get basic() { return tx("Basic (kullanıcı + parola)") }, get header() { return tx("Özel header") } }

function StateBadges({ transport }: { transport: { configured: boolean; enabled: boolean } }) {
  useLanguage()
  return <div className="flex flex-wrap gap-1.5"><StatusPill level={transport.configured ? 'safe' : 'neutral'}>{transport.configured ? tx('Yapılandırıldı') : tx('Yapılandırılmadı')}</StatusPill><StatusPill level={transport.enabled ? 'safe' : 'attention'}>{transport.enabled ? tx('Etkin') : tx('Devre dışı')}</StatusPill></div>
}

/** One write-only secret control: the value is sent and cleared from state, never shown again; the API only reports `hasSecret`. */
function SecretControl({ label, transport, onSet, onClear }: {
  label: string
  transport: { hasSecret: boolean; secretUpdatedAt: string | null }
  onSet: { mutate: (value: string) => void; isPending: boolean; error: unknown; reset: () => void }
  onClear: { mutate: () => void; isPending: boolean; error: unknown; reset: () => void }
}) {
  useLanguage()
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
    <h3 className="text-sm font-semibold text-text-heading">{tx("Kimlik bilgileri")}</h3>
    <p className="text-sm text-text-muted">{tx(label)}: {transport.hasSecret ? tx("kayıtlı ({0})", { "0": formatDateTime(transport.secretUpdatedAt, tx('Henüz bildirilmedi')) }) : tx('ayarlanmadı')}{tx(". Kayıtlı değer geri okunamaz.")}</p>
    <form onSubmit={submit} className="space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0 flex-1 basis-48"><Field label={tx("Yeni {0}", { "0": label.toLocaleLowerCase(currentLocale()) })}>{(control) => <input {...control} className={inputClass} type="password" autoComplete="new-password" value={value} onChange={(event) => setValue(event.target.value)} />}</Field></div>
        <Button type="submit" variant="outline" disabled={!value || onSet.isPending}>{onSet.isPending ? tx('Kaydediliyor…') : tx('Ayarla')}</Button>
        {transport.hasSecret && <Button type="button" variant="outline" disabled={onClear.isPending} onClick={() => { onSet.reset(); onClear.reset(); setConfirmClear(true) }}>{tx("Temizle")}</Button>}
      </div>
      {Boolean(error) && <ErrorNotice error={error} />}
    </form>
    <ConfirmDialog open={confirmClear} onClose={() => setConfirmClear(false)} title={tx("{0} temizlensin mi?", { "0": label })} description={tx("Kayıtlı kimlik bilgisi silinecek. Kimlik doğrulaması kullanan bildirim gönderimleri, yeni bir değer ayarlanana kadar başarısız olabilir.")} confirmLabel={tx("Gizli değeri temizle")} pending={onClear.isPending} onConfirm={() => { onClear.mutate(); setConfirmClear(false) }} />
  </div>
}

const blank = (text: string) => text.trim() || null

export function EmailTransportCard({ transport }: { transport: EmailTransport }) {
  useLanguage()
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

  return <Section title={tx("E-posta (SMTP)")} description={tx("Tenant e-posta bildirimleri bu SMTP sunucusu üzerinden gönderilir.")} actions={<StateBadges transport={transport} />}>
    <div className="space-y-6">
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />{tx("E-posta aktarımı etkin")}</label>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tx("Sunucu")}>{(control) => <input {...control} className={inputClass} value={host} onChange={(event) => setHost(event.target.value)} />}</Field>
        <Field label={tx("Port")}>{(control) => <input {...control} className={inputClass} inputMode="numeric" value={port} aria-invalid={!Number.isInteger(portNumber) || portNumber < 1 || portNumber > 65535} onChange={(event) => setPort(event.target.value)} />}</Field>
        <Field label={tx("Güvenlik")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={security} onChange={(event) => setSecurity(event.target.value as EmailSecurity)}>{(Object.keys(emailSecurityLabels) as EmailSecurity[]).map((item) => <option key={item} value={item}>{emailSecurityLabels[item]}</option>)}</select>}</Field>
        <Field label={tx("Kullanıcı adı")}>{(control) => <input {...control} className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} />}</Field>
        <Field label={tx("Gönderen adresi")}>{(control) => <input {...control} className={inputClass} value={fromAddress} onChange={(event) => setFromAddress(event.target.value)} />}</Field>
        <Field label={tx("Gönderen adı")}>{(control) => <input {...control} className={inputClass} value={fromName} onChange={(event) => setFromName(event.target.value)} />}</Field>
      </div>
      <p className="text-xs text-text-muted">{tx("“Şifreleme yok” seçeneği kullanıcı adı/parola yapılandırıldığında reddedilir.")}</p>
      {save.isError && <ErrorNotice error={save.error} />}
      {save.isSuccess && <Notice tone="success">{tx("E-posta aktarımı kaydedildi.")}</Notice>}
      <Button type="submit" disabled={save.isPending || !valid}>{save.isPending ? tx('Kaydediliyor…') : tx('E-posta ayarlarını kaydet')}</Button>
    </form>
    <SecretControl label={tx("SMTP parolası")} transport={transport} onSet={setSecret} onClear={clearSecret} />
    </div>
  </Section>
}

export function SmsTransportCard({ transport }: { transport: SmsTransport }) {
  useLanguage()
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

  return <Section title={tx("SMS ağ geçidi")} description={tx("Tenant SMS bildirimlerinin platform genelindeki gönderim ayarları.")} actions={<StateBadges transport={transport} />}>
    <div className="space-y-6">
    <Notice tone="warning" title={tx("UYGULANDI — DOĞRULANMADI")}>{tx("Yalnızca genel bir HTTP JSON bağdaştırıcısı uygulandı; hiçbir gerçek SMS sağlayıcısına karşı doğrulanmadı. Canlıya almadan önce kendi sağlayıcınızla deneyin.")}</Notice>
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />{tx("SMS aktarımı etkin")}</label>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tx("Sağlayıcı")}>{(control) => <input {...control} className={inputClass} value="generic-http" disabled readOnly />}</Field>
        <Field label={tx("Uç nokta (https://…)")}>{(control) => <input {...control} className={inputClass} value={endpoint} onChange={(event) => setEndpoint(event.target.value)} />}</Field>
        <Field label={tx("Kimlik doğrulama")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={authentication} onChange={(event) => setAuthentication(event.target.value as SmsAuthentication)}>{(Object.keys(smsAuthLabels) as SmsAuthentication[]).map((item) => <option key={item} value={item}>{smsAuthLabels[item]}</option>)}</select>}</Field>
        <Field label={tx("Gönderen")}>{(control) => <input {...control} className={inputClass} value={sender} onChange={(event) => setSender(event.target.value)} />}</Field>
        {authentication === 'basic' && <Field label={tx("Kullanıcı adı")}>{(control) => <input {...control} className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} />}</Field>}
        {authentication === 'header' && <Field label={tx("Header adı")}>{(control) => <input {...control} className={inputClass} value={headerName} onChange={(event) => setHeaderName(event.target.value)} />}</Field>}
      </div>
      {save.isError && <ErrorNotice error={save.error} />}
      {save.isSuccess && <Notice tone="success">{tx("SMS aktarımı kaydedildi.")}</Notice>}
      <Button type="submit" disabled={save.isPending || !endpoint.trim()}>{save.isPending ? tx('Kaydediliyor…') : tx('SMS ayarlarını kaydet')}</Button>
    </form>
    {authentication !== 'none' && <SecretControl label={authentication === 'bearer' ? tx('Bearer token') : authentication === 'basic' ? tx('Parola') : tx('Header değeri')} transport={transport} onSet={setSecret} onClear={clearSecret} />}
    </div>
  </Section>
}
