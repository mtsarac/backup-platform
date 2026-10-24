import { useEffect, useRef, useState } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { Tabs } from '@/components/ui/tabs'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useLanguage, tx } from '@/lib/i18n'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import { CopyButton } from '@/components/ui/copy-button'
import { API_BASE_URL } from '@/lib/api/client'
import { type ErrorDescription, accessDenied, describeError } from '@/lib/api/failure-messages'
import { createEnrollmentToken, getAgentDistribution, getEnrollmentTokenStatus, revokeEnrollmentToken } from './api'
import { agentQueryKeys } from './hooks'
import { enrollmentCommands, httpsServerOrigin, suggestedServerOrigin } from './enrollment-commands'
import type { AgentPlatform, InstallationMode } from './enrollment-commands'
import type { AgentDistribution } from './api'
import { useEnrollmentMonitor } from './use-enrollment-monitor'
import { formatDateTime } from '@/lib/format'

/** Three plain steps: which computer, the key and install command, then the connection. Mounted only for a tenant administrator while open; the key never leaves transient state. */
export function AddAgentDialog({ tenantId, onClose, onCreated }: { tenantId: string; onClose: () => void; onCreated?: () => void }) {
  const { language } = useLanguage()
  const client = useQueryClient()
  const [platform, setPlatform] = useState<AgentPlatform>('Windows')
  const [initialServer] = useState(() => httpsServerOrigin(import.meta.env.VITE_AGENT_PUBLIC_URL ?? '') || suggestedServerOrigin(API_BASE_URL, window.location.origin))
  const [server, setServer] = useState(initialServer)
  const [mode, setMode] = useState<InstallationMode>('interactive')
  const [distribution, setDistribution] = useState<AgentDistribution | null>(null)
  const [distributionError, setDistributionError] = useState<ErrorDescription | null>(null)
  const [name, setName] = useState('')
  const [lifetime, setLifetime] = useState('1440')
  const [ticket, setTicket] = useState<{ tokenId: string; expiresAt: string } | null>(null)
  const [secret, setSecret] = useState('')
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<ErrorDescription | null>(null)
  const request = useRef<AbortController | null>(null)
  const nameRef = useRef<HTMLInputElement>(null)
  const [step, setStep] = useState<'computer' | 'install'>('computer')
  // Opens the technical details once no safe server address could be found, so the one value the user must supply is in view.
  const [serverUnknown, setServerUnknown] = useState(false)
  const serverEdited = useRef(false)
  const monitor = useEnrollmentMonitor(tenantId, ticket, () => setSecret(''))
  useEffect(() => () => { request.current?.abort() }, [])
  useEffect(() => {
    const controller = new AbortController()
    void getAgentDistribution(controller.signal).then(result => {
      if (controller.signal.aborted) return
      setDistribution(result)
      const publicOrigin = httpsServerOrigin(import.meta.env.VITE_AGENT_PUBLIC_URL ?? '') || httpsServerOrigin(result.serverUrl ?? '')
      if (publicOrigin && !serverEdited.current) setServer(publicOrigin)
      if (!publicOrigin && !initialServer) setServerUnknown(true)
    }).catch(cause => { if (!controller.signal.aborted) { setDistributionError(describeError(cause)); if (!initialServer) setServerUnknown(true) } })
    return () => controller.abort()
  }, [initialServer])
  useEffect(() => {
    if (monitor.phase === 'Online') {
      void client.invalidateQueries({ queryKey: agentQueryKeys.all(tenantId) })
      void client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] })
    }
  }, [monitor.phase, client, tenantId])
  function close() { request.current?.abort(); setSecret(''); setTicket(null); onClose() }
  async function create() {
    if (pending || !httpsServerOrigin(server)) return
    const controller = new AbortController()
    request.current = controller
    setPending(true); setError(null); setSecret('')
    try {
      // Always resolve safe status before replacing: a consumption racing regeneration must not be revoked.
      if (ticket) {
        const status = await getEnrollmentTokenStatus(tenantId, ticket.tokenId, controller.signal)
        if (controller.signal.aborted) return
        if (status.state === 'Active') await revokeEnrollmentToken(tenantId, ticket.tokenId, controller.signal)
        if (controller.signal.aborted) return
      }
      setTicket(null)
      const result = await createEnrollmentToken(tenantId, Number(lifetime), controller.signal)
      if (controller.signal.aborted) return
      setSecret(result.token)
      setTicket({ tokenId: result.tokenId, expiresAt: result.expiresAt })
      void client.invalidateQueries({ queryKey: agentQueryKeys.tokens(tenantId) })
      void client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] })
      onCreated?.()
    } catch (cause) { if (!controller.signal.aborted) setError(describeError(cause)) }
    finally { if (!controller.signal.aborted) setPending(false) }
  }
  const labels = { Waiting: tx('Komutun çalıştırılması bekleniyor'), Checking: tx('Kayıt anahtarı gizlendi; kayıt durumu doğrulanıyor'), Enrolled: tx('Kayıt tamamlandı'), Connecting: tx('Bilgisayar bağlanıyor'), Online: tx('Çevrimiçi'), Expired: tx('Kayıt anahtarının süresi doldu'), Revoked: tx('Kayıt anahtarı iptal edildi'), Consumed: tx('Kayıt anahtarı kullanıldı; bilgisayar bilgisi bulunamadı') }
  const view = monitor.phase === 'Enrolled' || monitor.phase === 'Connecting' || monitor.phase === 'Online' ? 'connect' : step
  const packageReady = Boolean(distribution?.packages[platform === 'Windows' ? 'win-x64' : 'linux-x64'])
  const command = packageReady ? enrollmentCommands(platform, server, secret, name, mode, language) : ''
  const serverOk = Boolean(httpsServerOrigin(server))
  return <Dialog.Root open onOpenChange={(open) => { if (!open) close() }}>
    <Dialog.Portal><Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Dialog.Popup initialFocus={nameRef} className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-xl border border-border bg-surface p-6 shadow-xl">
        <Dialog.Title className="text-lg font-semibold text-text-heading">{tx("Yedekleme bilgisayarı ekle")}</Dialog.Title>
        <Dialog.Description className="text-sm text-text-muted">{tx("Yedekleri alacak bilgisayara küçük bir program kurulur. Kayıt anahtarı tek kullanımlıktır ve bu ekrandan kapandıktan sonra tekrar gösterilmez.")}</Dialog.Description>
        <Stepper steps={[tx('Bilgisayar'), tx('Kurulum'), tx('Bağlantı')]} current={view === 'computer' ? 0 : view === 'install' ? 1 : 2} />
        {view === 'computer' && <form id="add-agent" className="space-y-4" onSubmit={(event) => { event.preventDefault(); setStep('install') }}>
          <section className="space-y-4">
            <h2 className="font-medium text-text-heading">{tx("Hangi bilgisayara kurulacak?")}</h2>
            <p className="text-sm text-text-muted"><span className="font-medium text-text-heading">{tx("Neden soruluyor?")}</span>{' '}{tx("Ad, bilgisayarı listede tanımanızı sağlar; işletim sistemi doğru kurulum komutunu seçmemize yarar.")}</p>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={tx("Bilgisayarın adı")} hint={mode === 'automatic' ? tx("Tek komutla kurulum için ad gereklidir.") : tx("İsteğe bağlı; boş bırakırsanız kurulum sırasında sorulur.")}>{(control) => <input {...control} ref={nameRef} disabled={pending} value={name} maxLength={200} onChange={event => setName(event.target.value)} className={inputClass} />}</Field>
              <Field label={tx("İşletim sistemi")}>{(control) => <select {...control} disabled={pending} value={platform} onChange={event => setPlatform(event.target.value as AgentPlatform)} className={`${selectClass} w-full`}><option>Windows</option><option>Linux</option></select>}</Field>
            </div>
          </section>
          <TechnicalDetails items={[]} defaultOpen={serverUnknown}>
            <Field label={tx("Backend HTTPS adresi")} hint={serverOk ? undefined : <span className="text-warning">{tx("Bilgisayarın erişebildiği HTTPS sunucu adresini girin (yalnızca protokol, sunucu ve port). HTTP geliştirme proxy’si güvenli bir adres belirlemez.")}</span>}>{(control) => <input {...control} required type="url" disabled={pending} value={server} placeholder="https://backup.example.com" onChange={event => { serverEdited.current = true; setServer(event.target.value) }} className={inputClass} />}</Field>
            <Field label={tx("Geçerlilik süresi (dakika)")}>{(control) => <input {...control} required type="number" min={5} max={10080} step={1} disabled={pending} value={lifetime} onChange={event => setLifetime(event.target.value)} className={inputClass} />}</Field>
          </TechnicalDetails>
        </form>}
        {view === 'install' && <section className="space-y-3">
          <h2 className="font-medium text-text-heading">{tx("Kurulum komutunu bilgisayarda çalıştırın")}</h2>
          <Tabs label={tx('Kurulum şekli')} value={mode} onChange={setMode} idPrefix="agent-install-mode" items={[{ value: 'interactive', label: tx('Adım adım kurulum') }, { value: 'automatic', label: tx('Tek komutla kurulum') }]} />
          <div role="tabpanel" id={`agent-install-mode-panel-${mode}`} aria-labelledby={`agent-install-mode-tab-${mode}`} className="space-y-2 rounded-lg border border-border bg-surface-2 p-4 text-sm text-text-muted">
            <p>{mode === 'interactive' ? tx('Kurulum eksik bilgileri sorar. Kayıt anahtarını istendiğinde yapıştırın; komut anahtarı içermez.') : tx('Kurulum her şeyi soru sormadan yapar; komut kayıt anahtarını içerir. Kopyalamadan önce bilgisayarın adını girin.')}</p>
            <p>{platform === 'Windows' ? tx('Hedef bilgisayarda PowerShell’i yönetici olarak açın.') : tx('Hedef bilgisayarda yönetici (sudo) yetkisi olan bir terminal açın. Kurulum bir sistem hizmeti oluşturur.')}</p>
            {!distribution && !distributionError && <p>{tx('Kurulum dosyalarının hazır olup olmadığı denetleniyor…')}</p>}
            {distributionError && <ErrorNotice {...distributionError} />}
            {distribution && !packageReady && <ErrorNotice {...accessDenied(tx('Bu işletim sistemi için kurulum paketi yayımlanmamış. Yöneticinizle iletişime geçin.'))} />}
          </div>
          {!ticket && <p className="text-sm text-text-muted">{tx("Anahtarı oluşturduğunuzda komut burada görünür.")}</p>}
          {ticket && secret && <div className="space-y-3">
            {mode === 'interactive' && <><Field label={tx('Tek kullanımlık kayıt anahtarı')}>{control => <textarea {...control} readOnly value={secret} rows={2} className={inputClass} />}</Field><CopyButton value={secret} label={tx('Kayıt anahtarını kopyala')} /></>}
            <Field label={tx("Tek kullanımlık kurulum komutu")}>{(control) => <textarea {...control} readOnly rows={4} value={command} className="mt-1 block w-full resize-none break-all rounded-lg border border-border-strong/70 bg-surface p-3 font-mono text-sm text-text" />}</Field>
            <CopyButton value={command} label={tx("Kurulum komutunu kopyala")} />
            {mode === 'automatic' && !name.trim() && <Notice tone="warning">{tx('Tek komutla kurulum komutunu oluşturmak için bilgisayarın adını girin.')}</Notice>}
            <p className="text-sm text-text-muted">{tx('Anahtarı paylaşmayın; günlüklere, ayar dosyalarına veya komut geçmişine kaydetmeyin. Kapatıldıktan sonra tekrar gösterilmez.')} {tx('Son kullanma:')} {formatDateTime(ticket.expiresAt, tx('Henüz bildirilmedi'))}</p>
          </div>}
          {ticket && <>
            <p role="status" className="rounded-lg border border-border bg-surface px-3 py-2 font-medium text-text">{labels[monitor.phase]}</p>
            {monitor.phase === 'Waiting' && <p className="text-sm text-text-muted">{tx("Komutu hedef bilgisayarda çalıştırın. Kayıt ve ilk bağlantı bekleniyor; bu sayfayı açık bırakın.")}</p>}
          </>}
          <TechnicalDetails items={[]}><p>{tx('Sunucunun TLS sertifikası hedef bilgisayarda güvenilir olmalıdır. Sertifika doğrulamasını asla devre dışı bırakmayın.')}</p></TechnicalDetails>
        </section>}
        {view === 'connect' && <section className="space-y-3">
          <h2 className="font-medium text-text-heading">{tx("Bilgisayar bağlanıyor")}</h2>
          <p role="status" className="rounded-lg border border-border bg-surface px-3 py-2 font-medium text-text">{labels[monitor.phase]}</p>
          {monitor.phase !== 'Online' && <p className="text-sm text-text-muted">{tx("Kimlik kaydedildi. Yedekleme programının çalıştığını ve sunucuya erişebildiğini kontrol edin; bağlantı bekleniyor.")}</p>}
          {monitor.agent && <>
            <Facts><Fact label={tx("Ad")}>{monitor.agent.hostName ?? tx('Bildirilmedi')}</Fact><Fact label={tx("Bağlantı")}>{monitor.agent.connectivity === 'online' ? tx('Çevrimiçi') : monitor.agent.connectivity === 'offline' ? tx('Çevrimdışı') : tx('Bilinmiyor')}</Fact></Facts>
            <TechnicalDetails items={[
              { label: tx('Bilgisayar kimliği'), value: monitor.agent.agentId },
              { label: tx('İşletim sistemi'), value: monitor.agent.osDescription ?? monitor.agent.platform },
              ...(monitor.agent.agentVersion ? [{ label: tx('Yedekleme programı sürümü'), value: monitor.agent.agentVersion }] : []),
            ]} />
          </>}
          {monitor.phase === 'Online' && <p className="text-sm font-medium text-success">{tx("Bilgisayar çevrimiçi. Kurulum tamamlandı.")}</p>}
        </section>}
        {(error || monitor.error) && <ErrorNotice {...(error ?? monitor.error!)} />}
        <div className="flex flex-wrap justify-end gap-2">
          {view === 'install' && <Button variant="outline" disabled={pending} onClick={() => setStep('computer')}>{tx("Geri")}</Button>}
          <Dialog.Close render={<Button variant="outline" />}>{monitor.phase === 'Online' ? tx('Tamam') : tx('Kapat')}</Dialog.Close>
          {monitor.paused && <Button variant="outline" onClick={monitor.retry}>{tx("Takibi tekrar dene")}</Button>}
          {view === 'computer' && <Button type="submit" form="add-agent" disabled={!serverOk}>{tx("İleri")}</Button>}
          {view === 'install' && <Button disabled={pending || !serverOk} onClick={() => void create()}>{pending ? tx('Oluşturuluyor…') : ticket ? tx('Yeni kayıt anahtarı oluştur') : tx('Kayıt anahtarı oluştur')}</Button>}
        </div>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
