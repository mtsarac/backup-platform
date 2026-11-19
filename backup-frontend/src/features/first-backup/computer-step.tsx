import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { CopyButton } from '@/components/ui/copy-button'
import { Field } from '@/components/ui/field'
import { ErrorNotice } from '@/components/ui/notice'
import { Select, TextArea, TextInput } from '@/components/ui/text-input'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { createEnrollmentToken } from '@/features/agents/api'
import { enrollmentCommands, httpsServerOrigin, likelyPlatform, type AgentPlatform } from '@/features/agents/enrollment-commands'
import { agentQueryKeys, useAgentDistribution } from '@/features/agents/hooks'
import { useEnrollmentMonitor } from '@/features/agents/use-enrollment-monitor'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'

/** After this long without a connection the screen says so in a sentence; it keeps waiting. */
const SLOW_AFTER_MS = 120_000

/** First screen of the first-backup flow: name and system, one install command and the live status. Calls `onOnline` once the new computer is online. The key never leaves transient state. */
export function ComputerStep({ tenantId, onOnline }: { tenantId: string; onOnline: () => void | Promise<void> }) {
  const { language } = useLanguage()
  const client = useQueryClient()
  const distribution = useAgentDistribution()
  const [platform, setPlatform] = useState<AgentPlatform>(likelyPlatform)
  const [name, setName] = useState('')
  const [ticket, setTicket] = useState<{ tokenId: string; expiresAt: string } | null>(null)
  const [secret, setSecret] = useState('')
  const [pending, setPending] = useState(false)
  const [failure, setFailure] = useState<ErrorDescription | null>(null)
  const [slow, setSlow] = useState(false)
  const request = useRef<AbortController | null>(null)
  const monitor = useEnrollmentMonitor(tenantId, ticket, () => setSecret(''))
  const advance = useRef(onOnline)
  useEffect(() => { advance.current = onOnline })
  useEffect(() => () => { request.current?.abort() }, [])
  useEffect(() => { if (monitor.phase === 'Online') void advance.current() }, [monitor.phase])
  useEffect(() => {
    if (!ticket) return
    const timer = window.setTimeout(() => setSlow(true), SLOW_AFTER_MS)
    return () => { window.clearTimeout(timer); setSlow(false) }
  }, [ticket])

  const server = httpsServerOrigin(distribution.data?.serverUrl ?? '')
  const packageKey = platform === 'Windows' ? 'win-x64' : 'linux-x64'
  const packageReady = Boolean(distribution.data?.packages[packageKey])
  const ready = Boolean(server && packageReady)
  const command = ready ? enrollmentCommands(platform, server, secret, name, language) : ''
  const ended = monitor.phase === 'Expired' || monitor.phase === 'Revoked' || monitor.phase === 'Consumed'
  const labels = { Waiting: tx('Komutun çalıştırılması bekleniyor'), Checking: tx('Kayıt anahtarı gizlendi; kayıt durumu doğrulanıyor'), Enrolled: tx('Kayıt tamamlandı'), Connecting: tx('Bilgisayar bağlanıyor'), Online: tx('Çevrimiçi. Kurulum tamamlandı.'), Expired: tx('Kayıt anahtarının süresi doldu'), Revoked: tx('Kayıt anahtarı iptal edildi'), Consumed: tx('Kayıt anahtarı kullanıldı; bilgisayar bilgisi bulunamadı') }

  async function create() {
    if (pending || !ready || !name.trim()) return
    const controller = new AbortController()
    request.current = controller
    setPending(true); setFailure(null)
    try {
      const result = await createEnrollmentToken(tenantId, controller.signal)
      if (controller.signal.aborted) return
      setSecret(result.token)
      setTicket({ tokenId: result.tokenId, expiresAt: result.expiresAt })
      void client.invalidateQueries({ queryKey: agentQueryKeys.tokens(tenantId) })
      void client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] })
    } catch (cause) { if (!controller.signal.aborted) setFailure(describeError(cause)) }
    finally { if (!controller.signal.aborted) setPending(false) }
  }

  return <section aria-labelledby="first-backup-computer" className="space-y-4">
    <h2 id="first-backup-computer" className="text-lg font-semibold text-text-heading">{tx('Yedekleme bilgisayarı ekle')}</h2>
    <p className="text-sm text-text-muted">{tx('Yedekleri alacak bilgisayara küçük bir program kurulur.')}</p>
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label={tx('Bilgisayarın adı')} hint={tx('Listede bilgisayarı bu adla tanırsınız.')}>{control => <TextInput {...control} required autoFocus value={name} maxLength={200} onChange={event => setName(event.target.value)} />}</Field>
      <Field label={tx('İşletim sistemi')}>{control => <Select {...control} value={platform} onChange={event => setPlatform(event.target.value as AgentPlatform)}><option>Windows</option><option>Linux</option></Select>}</Field>
    </div>
    {distribution.isPending && <p className="text-sm text-text-muted">{tx('Kurulum dosyalarının hazır olup olmadığı denetleniyor…')}</p>}
    {distribution.error && <ErrorNotice error={distribution.error} onRetry={() => void distribution.refetch()} />}
    {distribution.data && !server && <ErrorNotice what={tx('Sunucu HTTPS adresi ayarlanmamış.')} action={tx('Platform yöneticinizden sunucu HTTPS adresini ayarlamasını isteyin.')} />}
    {distribution.data && server && !packageReady && <ErrorNotice what={tx('Bu işletim sistemi için kurulum paketi yayımlanmamış.')} action={tx('Platform yöneticinizden kurulum paketini yayımlamasını isteyin.')} />}
    {!ticket && <div><Button type="button" disabled={pending || !ready || !name.trim()} onClick={() => void create()}>{pending ? tx('Oluşturuluyor…') : tx('Kurulum komutunu oluştur')}</Button></div>}
    {failure && <ErrorNotice {...failure} onRetry={() => void create()} />}
    {ticket && secret && command && <div className="space-y-2">
      <p className="text-sm text-text-muted">{platform === 'Windows' ? tx('Hedef bilgisayarda PowerShell’i yönetici olarak açın.') : tx('Hedef bilgisayarda yönetici (sudo) yetkisi olan bir terminal açın. Kurulum bir sistem hizmeti oluşturur.')}</p>
      <Field label={tx('Tek kullanımlık kurulum komutu')}>{control => <TextArea {...control} readOnly rows={4} value={command} className="resize-none break-all font-mono text-sm" />}</Field>
      <CopyButton value={command} label={tx('Kurulum komutunu kopyala')} />
      <p className="text-sm text-text-muted">{tx('Bu anahtar tek kullanımlıktır ve süresi dolar.')} {tx('Anahtarı paylaşmayın; günlüklere, ayar dosyalarına veya komut geçmişine kaydetmeyin. Kapatıldıktan sonra tekrar gösterilmez.')} {tx('Son kullanma:')} {formatDateTime(ticket.expiresAt, tx('Henüz bildirilmedi'))}</p>
    </div>}
    {ticket && <div className="space-y-2">
      <p role="status" className="rounded-lg border border-border bg-surface px-3 py-2 font-medium text-text">{labels[monitor.phase]}</p>
      {monitor.phase === 'Waiting' && <p className="text-sm text-text-muted">{tx('Komutu hedef bilgisayarda çalıştırın. Kayıt ve ilk bağlantı bekleniyor; bu sayfayı açık bırakın.')}</p>}
      {slow && !ended && monitor.phase !== 'Online' && <p className="text-sm text-text-muted">{tx('Beklenenden uzun sürüyor. Komutun bilgisayarda hata vermeden bittiğinden ve bilgisayarın bu sunucuya erişebildiğinden emin olun; bu sayfa beklemeye devam eder.')}</p>}
      {ended && <Button type="button" variant="outline" disabled={pending} onClick={() => void create()}>{tx('Yeni komut oluştur')}</Button>}
      {monitor.paused && <Button type="button" variant="outline" onClick={monitor.retry}>{tx('Takibi tekrar dene')}</Button>}
      {monitor.error && <ErrorNotice {...monitor.error} />}
    </div>}
    {ticket && secret && <details className="text-sm">
      <summary className="w-fit cursor-pointer font-medium text-text-muted">{tx('Başka yollarla kur')}</summary>
      <div className="space-y-2 pt-2">
        <Field label={tx('Tek kullanımlık kayıt anahtarı')}>{control => <TextArea {...control} readOnly value={secret} rows={2} />}</Field>
        <CopyButton value={secret} label={tx('Kayıt anahtarını kopyala')} />
        {platform === 'Windows' && distribution.data?.packages['win-x64-setup'] && <div className="space-y-1">
          <p><a className="text-primary underline" href={`${server}/api/agent-dist/packages/win-x64-setup`} download>{tx('Kurulum sihirbazını indir (.exe)')}</a></p>
          <p className="text-text-muted">{tx('Sihirbaz yalnızca yukarıdaki anahtarı ve bilgisayarın adını sorar.')} {tx('Program henüz dijital olarak imzalı olmadığından Windows SmartScreen bir uyarı gösterebilir; “Ek bilgi” ve “Yine de çalıştır” ile devam edin.')}</p>
        </div>}
        <p><a className="text-primary underline" href={`${server}/api/agent-dist/${platform === 'Windows' ? 'install.ps1' : 'install.sh'}`} download>{tx('Kurulum scriptini indir')}</a></p>
        <p><a className="text-primary underline" href={`${server}/api/agent-dist/packages/${packageKey}`} download>{platform === 'Windows' ? tx('Windows zip paketini indir') : tx('Linux tar.gz paketini indir')}</a></p>
      </div>
    </details>}
    {ticket && <TechnicalDetails items={[]}><p>{tx('Sunucunun TLS sertifikası hedef bilgisayarda güvenilir olmalıdır. Sertifika doğrulamasını asla devre dışı bırakmayın.')}</p></TechnicalDetails>}
  </section>
}
