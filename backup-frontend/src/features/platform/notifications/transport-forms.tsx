import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { SecretField } from '@/components/ui/secret-field'
import { useSecretValue } from '@/components/ui/secret-value'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import {
  useClearEmailSecret, useClearSmsSecret, useSetEmailSecret, useSetSmsSecret, useUpdateEmailTransport, useUpdateSmsTransport,
} from './hooks'
import type { EmailSecurity, EmailTransport, SmsAuthentication, SmsTransport } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'

const emailSecurityLabels: Record<EmailSecurity, string> = { starttls: 'STARTTLS', get implicit() { return tx("Implicit TLS") }, get none() { return tx("Şifreleme yok") } }
const smsAuthLabels: Record<SmsAuthentication, string> = { get none() { return tx("Yok") }, get bearer() { return tx("Bearer token") }, get basic() { return tx("Basic (kullanıcı + parola)") }, get header() { return tx("Özel header") } }

/** One plain answer: does this way of sending alerts work right now? `configured` already includes the secret the transport needs. */
function verdict(transport: { configured: boolean; enabled: boolean }, channel: string) {
  if (transport.configured && transport.enabled) return { level: 'safe', label: tx('Çalışıyor'), sentence: tx("{0} bildirimleri gönderiliyor.", { "0": channel }) } as const
  if (transport.configured) return { level: 'attention', label: tx('Kapalı'), sentence: tx("{0} ayarları tamam ama aktarım kapalı; bildirim gönderilmiyor.", { "0": channel }) } as const
  if (transport.enabled) return { level: 'urgent', label: tx('Ayarlar eksik'), sentence: tx("{0} aktarımı açık ama ayarlar eksik; bildirim gönderilemiyor.", { "0": channel }) } as const
  return { level: 'neutral', label: tx('Kurulmadı'), sentence: tx("{0} henüz kurulmadı; bu yoldan bildirim gönderilemez.", { "0": channel }) } as const
}

const when = (value: string | null) => formatDateTime(value, tx('Henüz bildirilmedi'))
const secretItems = (transport: { secretUpdatedAt: string | null; updatedAt: string | null }): TechnicalItem[] => [
  { label: tx("Gizli değerin ayarlandığı zaman"), value: when(transport.secretUpdatedAt) },
  { label: tx("Son güncelleme"), value: when(transport.updatedAt) },
]

/** One write-only secret control: the value is sent and cleared from state, never shown again; the API only reports `hasSecret`. */
function SecretControl({ label, transport, onSet, onClear }: {
  label: string
  transport: { hasSecret: boolean }
  onSet: { mutate: (value: string) => void; isPending: boolean; error: unknown; reset: () => void }
  onClear: { mutate: () => void; isPending: boolean; error: unknown; reset: () => void }
}) {
  useLanguage()
  const secret = useSecretValue()
  const [confirmClear, setConfirmClear] = useState(false)
  function submit(event: FormEvent) {
    event.preventDefault()
    const next = secret.take()
    onClear.reset()
    onSet.mutate(next)
  }
  const error = onSet.error ?? onClear.error
  return <div className="space-y-3 border-t border-border pt-5">
    <h3 className="text-sm font-semibold text-text-heading">{tx("Kimlik bilgileri")}</h3>
    <p className="text-sm text-text-muted">{transport.hasSecret ? tx("{0}: kayıtlı. Kayıtlı değer geri okunamaz.", { "0": label }) : tx("{0}: ayarlanmadı.", { "0": label })}</p>
    <form onSubmit={submit} className="space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0 flex-1 basis-48"><SecretField label={tx("Yeni {0}", { "0": label.toLocaleLowerCase(currentLocale()) })} saved={transport.hasSecret} value={secret.value} onChange={secret.set} /></div>
        <Button type="submit" variant="outline" disabled={!secret.value || onSet.isPending}>{onSet.isPending ? tx('Kaydediliyor…') : tx('Ayarla')}</Button>
        {transport.hasSecret && <Button type="button" variant="outline" disabled={onClear.isPending} onClick={() => { onSet.reset(); onClear.reset(); setConfirmClear(true) }}>{tx("Temizle")}</Button>}
      </div>
      {Boolean(error) && <ErrorNotice error={error} />}
    </form>
    <ConfirmDialog open={confirmClear} onClose={() => setConfirmClear(false)} title={tx("{0} temizlensin mi?", { "0": label })}
      consequence={{
        what: tx("Kayıtlı kimlik bilgisi silinir; yeni bir değer ayarlanana kadar kimlik doğrulaması gerektiren bildirim gönderimleri başarısız olabilir."),
        who: tx("Bu kanaldan bildirim alan tüm çalışma alanları etkilenir."),
        reversible: tx("Evet. Yeni bir değer ayarlayabilirsiniz; silinen değer geri gelmez."),
      }}
      confirmLabel={tx("Gizli değeri temizle")} pending={onClear.isPending} onConfirm={() => { onClear.mutate(); setConfirmClear(false) }} />
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

  const status = verdict(transport, tx('E-posta'))
  return <Section title={tx("E-posta (SMTP)")} description={tx("Tenant e-posta bildirimleri bu SMTP sunucusu üzerinden gönderilir.")} actions={<StatusPill level={status.level}>{status.label}</StatusPill>}>
    <div className="space-y-6">
    <p className="text-sm text-text">{status.sentence}</p>
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />{tx("E-posta aktarımı etkin")}</label>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tx("Gönderen adresi")}>{(control) => <input {...control} className={inputClass} value={fromAddress} onChange={(event) => setFromAddress(event.target.value)} />}</Field>
        <Field label={tx("Gönderen adı")}>{(control) => <input {...control} className={inputClass} value={fromName} onChange={(event) => setFromName(event.target.value)} />}</Field>
      </div>
      <TechnicalDetails items={[
        { label: tx("Kayıtlı sunucu"), value: transport.host ?? '—' },
        { label: tx("Kayıtlı bağlantı noktası"), value: transport.port === null ? '—' : String(transport.port) },
        { label: tx("Kayıtlı güvenlik"), value: emailSecurityLabels[transport.security] },
        { label: tx("Kayıtlı kullanıcı adı"), value: transport.username ?? '—' },
        ...secretItems(transport),
      ]}>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tx("Sunucu")}>{(control) => <input {...control} className={inputClass} value={host} onChange={(event) => setHost(event.target.value)} />}</Field>
        <Field label={tx("Port")}>{(control) => <input {...control} className={inputClass} inputMode="numeric" value={port} aria-invalid={!Number.isInteger(portNumber) || portNumber < 1 || portNumber > 65535} onChange={(event) => setPort(event.target.value)} />}</Field>
        <Field label={tx("Güvenlik")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={security} onChange={(event) => setSecurity(event.target.value as EmailSecurity)}>{(Object.keys(emailSecurityLabels) as EmailSecurity[]).map((item) => <option key={item} value={item}>{emailSecurityLabels[item]}</option>)}</select>}</Field>
        <Field label={tx("Kullanıcı adı")}>{(control) => <input {...control} className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} />}</Field>
      </div>
      <p className="text-xs text-text-muted">{tx("“Şifreleme yok” seçeneği kullanıcı adı/parola yapılandırıldığında reddedilir.")}</p>
      </TechnicalDetails>
      {!valid && <p className="text-sm text-text-muted">{tx("Kaydetmek için sunucu, geçerli bir bağlantı noktası ve gönderen adresi gerekir. Sunucu bilgileri “Teknik ayrıntılar” altındadır.")}</p>}
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

  const status = verdict(transport, tx('SMS'))
  return <Section title={tx("SMS ağ geçidi")} description={tx("Tenant SMS bildirimlerinin platform genelindeki gönderim ayarları.")} actions={<StatusPill level={status.level}>{status.label}</StatusPill>}>
    <div className="space-y-6">
    <p className="text-sm text-text">{status.sentence}</p>
    <Notice tone="warning" title={tx("UYGULANDI — DOĞRULANMADI")}>{tx("Yalnızca genel bir HTTP JSON bağdaştırıcısı uygulandı; hiçbir gerçek SMS sağlayıcısına karşı doğrulanmadı. Canlıya almadan önce kendi sağlayıcınızla deneyin.")}</Notice>
    <form onSubmit={submit} noValidate className="space-y-4">
      <label className="flex items-center gap-2 text-sm text-text"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />{tx("SMS aktarımı etkin")}</label>
      <Field label={tx("Gönderen")}>{(control) => <input {...control} className={inputClass} value={sender} onChange={(event) => setSender(event.target.value)} />}</Field>
      <TechnicalDetails items={[
        { label: tx("Kayıtlı uç nokta"), value: transport.endpoint ?? '—' },
        { label: tx("Kayıtlı kimlik doğrulama"), value: smsAuthLabels[transport.authentication] },
        ...secretItems(transport),
      ]}>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tx("Sağlayıcı")}>{(control) => <input {...control} className={inputClass} value="generic-http" disabled readOnly />}</Field>
        <Field label={tx("Uç nokta (https://…)")}>{(control) => <input {...control} className={inputClass} value={endpoint} onChange={(event) => setEndpoint(event.target.value)} />}</Field>
        <Field label={tx("Kimlik doğrulama")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={authentication} onChange={(event) => setAuthentication(event.target.value as SmsAuthentication)}>{(Object.keys(smsAuthLabels) as SmsAuthentication[]).map((item) => <option key={item} value={item}>{smsAuthLabels[item]}</option>)}</select>}</Field>
        {authentication === 'basic' && <Field label={tx("Kullanıcı adı")}>{(control) => <input {...control} className={inputClass} value={username} autoComplete="off" onChange={(event) => setUsername(event.target.value)} />}</Field>}
        {authentication === 'header' && <Field label={tx("Header adı")}>{(control) => <input {...control} className={inputClass} value={headerName} onChange={(event) => setHeaderName(event.target.value)} />}</Field>}
      </div>
      </TechnicalDetails>
      {!endpoint.trim() && <p className="text-sm text-text-muted">{tx("Kaydetmek için uç nokta gerekir. Uç nokta “Teknik ayrıntılar” altındadır.")}</p>}
      {save.isError && <ErrorNotice error={save.error} />}
      {save.isSuccess && <Notice tone="success">{tx("SMS aktarımı kaydedildi.")}</Notice>}
      <Button type="submit" disabled={save.isPending || !endpoint.trim()}>{save.isPending ? tx('Kaydediliyor…') : tx('SMS ayarlarını kaydet')}</Button>
    </form>
    {authentication !== 'none' && <SecretControl label={authentication === 'bearer' ? tx('Bearer token') : authentication === 'basic' ? tx('Parola') : tx('Header değeri')} transport={transport} onSet={setSecret} onClear={clearSecret} />}
    </div>
  </Section>
}
