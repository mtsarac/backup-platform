import { useEffect, useRef, useState } from 'react'
import { Select, TextArea, TextInput } from '@/components/ui/text-input'
import { Dialog } from '@base-ui/react/dialog'
import { useQueryClient } from '@tanstack/react-query'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice } from '@/components/ui/notice'
import { Fact, Facts } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useLanguage, tx } from '@/lib/i18n'
import { CopyButton } from '@/components/ui/copy-button'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { createEnrollmentToken, getAgentDistribution, getEnrollmentTokenStatus, revokeEnrollmentToken } from './api'
import { agentQueryKeys } from './hooks'
import { enrollmentCommands, httpsServerOrigin } from './enrollment-commands'
import type { AgentPlatform } from './enrollment-commands'
import type { AgentDistribution } from './api'
import { useEnrollmentMonitor } from './use-enrollment-monitor'
import { formatDateTime } from '@/lib/format'

/** Three plain steps: which computer, the key and install command, then the connection. Mounted only for a tenant administrator while open; the key never leaves transient state. */
export function AddAgentDialog({ tenantId, onClose, onCreated }: { tenantId: string; onClose: () => void; onCreated?: () => void }) {
  const { language } = useLanguage()
  const client = useQueryClient()
  const [platform, setPlatform] = useState<AgentPlatform>('Windows')
  const [distribution, setDistribution] = useState<AgentDistribution | null>(null)
  // The platform administrator owns the server address; tenants only consume it.
  const server = httpsServerOrigin(distribution?.serverUrl ?? '')
  const [distributionError, setDistributionError] = useState<ErrorDescription | null>(null)
  const [name, setName] = useState('')
  const [ticket, setTicket] = useState<{ tokenId: string; expiresAt: string } | null>(null)
  const [secret, setSecret] = useState('')
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<ErrorDescription | null>(null)
  const request = useRef<AbortController | null>(null)
  const nameRef = useRef<HTMLInputElement>(null)
  const [step, setStep] = useState<'computer' | 'install'>('computer')
  const monitor = useEnrollmentMonitor(tenantId, ticket, () => setSecret(''))
  useEffect(() => () => { request.current?.abort() }, [])
  useEffect(() => {
    const controller = new AbortController()
    void getAgentDistribution(controller.signal).then(result => {
      if (!controller.signal.aborted) setDistribution(result)
    }).catch(cause => { if (!controller.signal.aborted) setDistributionError(describeError(cause)) })
    return () => controller.abort()
  }, [])
  useEffect(() => {
    if (monitor.phase === 'Online') {
      void client.invalidateQueries({ queryKey: agentQueryKeys.all(tenantId) })
      void client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] })
    }
  }, [monitor.phase, client, tenantId])
  function close() { request.current?.abort(); setSecret(''); setTicket(null); onClose() }
  async function create() {
    if (pending || !server || !name.trim() || !distribution?.packages[platform === 'Windows' ? 'win-x64' : 'linux-x64']) return
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
      const result = await createEnrollmentToken(tenantId, controller.signal)
      if (controller.signal.aborted) return
      setSecret(result.token)
      setTicket({ tokenId: result.tokenId, expiresAt: result.expiresAt })
      void client.invalidateQueries({ queryKey: agentQueryKeys.tokens(tenantId) })
      void client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] })
      onCreated?.()
    } catch (cause) { if (!controller.signal.aborted) setError(describeError(cause)) }
    finally { if (!controller.signal.aborted) setPending(false) }
  }
  const labels = { Waiting: tx('Komutun çalıştırılması bekleniyor'), Checking: tx('Kayıt anahtarı gizlendi; kayıt durumu doğrulanıyor'), Enrolled: tx('Kayıt tamamlandı'), Connecting: tx('Bilgisayar bağlanıyor'), Online: tx('Çevrimiçi. Kurulum tamamlandı.'), Expired: tx('Kayıt anahtarının süresi doldu'), Revoked: tx('Kayıt anahtarı iptal edildi'), Consumed: tx('Kayıt anahtarı kullanıldı; bilgisayar bilgisi bulunamadı') }
  const view = monitor.phase === 'Enrolled' || monitor.phase === 'Connecting' || monitor.phase === 'Online' ? 'connect' : step
  const packageReady = Boolean(distribution?.packages[platform === 'Windows' ? 'win-x64' : 'linux-x64'])
  const command = packageReady ? enrollmentCommands(platform, server, secret, name, language) : ''
  return <Dialog.Root open onOpenChange={(open) => { if (!open) close() }}>
    <Dialog.Portal><Dialog.Backdrop className="fixed inset-0 z-40 bg-scrim" />
      <Dialog.Popup initialFocus={nameRef} className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-lg border border-border bg-surface-raised p-6 shadow-overlay">
        <Dialog.Title className="text-lg font-semibold text-text-heading">{tx("Yedekleme bilgisayarı ekle")}</Dialog.Title>
        <Dialog.Description className="text-sm text-text-muted">{tx("Yedekleri alacak bilgisayara küçük bir program kurulur.")}</Dialog.Description>
        <Stepper steps={[tx('Bilgisayar'), tx('Kurulum'), tx('Bağlantı')]} current={view === 'computer' ? 0 : view === 'install' ? 1 : 2} />
        {view === 'computer' && <form id="add-agent" className="space-y-4" onSubmit={(event) => { event.preventDefault(); if (name.trim() && server && packageReady) setStep('install') }}>
          <section className="space-y-4">
            <h2 className="font-medium text-text-heading">{tx("Hangi bilgisayara kurulacak?")}</h2>
            <p className="text-sm text-text-muted"><span className="font-medium text-text-heading">{tx("Neden soruluyor?")}</span>{' '}{tx("Ad, bilgisayarı listede tanımanızı sağlar; işletim sistemi doğru kurulum komutunu seçmemize yarar.")}</p>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={tx("Bilgisayarın adı")} hint={tx("Tek komutla kurulum için ad gereklidir.")}>{(control) => <TextInput {...control} ref={nameRef} required disabled={pending} value={name} maxLength={200} onChange={event => setName(event.target.value)} />}</Field>
              <Field label={tx("İşletim sistemi")}>{(control) => <Select {...control} disabled={pending} value={platform} onChange={event => setPlatform(event.target.value as AgentPlatform)}><option>Windows</option><option>Linux</option></Select>}</Field>
            </div>
          </section>
          {distribution && !server && <ErrorNotice what={tx("Sunucu HTTPS adresi ayarlanmamış.")} action={tx("Platform yöneticinizden sunucu HTTPS adresini ayarlamasını isteyin.")} />}
          {distribution && server && !packageReady && <ErrorNotice what={tx("Bu işletim sistemi için kurulum paketi yayımlanmamış.")} action={tx("Platform yöneticinizden kurulum paketini yayımlamasını isteyin.")} />}
          {distributionError && <ErrorNotice {...distributionError} />}
        </form>}
        {view === 'install' && <section className="space-y-3">
          <h2 className="font-medium text-text-heading">{tx("Kurulum komutunu bilgisayarda çalıştırın")}</h2>
          <Card tone="subtle" className="space-y-2 text-sm text-text-muted">
            <p>{tx('Komut programı kurar, bilgisayarı kaydeder ve bağlar; soru sormaz. Kayıt anahtarını içerir.')}</p>
            <p>{platform === 'Windows' ? tx('Hedef bilgisayarda PowerShell’i yönetici olarak açın.') : tx('Hedef bilgisayarda yönetici (sudo) yetkisi olan bir terminal açın. Kurulum bir sistem hizmeti oluşturur.')}</p>
            <p>{tx("Bu bilgisayarda zaten bir ajan kuruluysa komut çalışmaz; önce mevcut ajanı kaldırın.")}</p>
            {!distribution && !distributionError && <p>{tx('Kurulum dosyalarının hazır olup olmadığı denetleniyor…')}</p>}
            {distributionError && <ErrorNotice {...distributionError} />}
            {distribution && !packageReady && <ErrorNotice what={tx("Bu işletim sistemi için kurulum paketi yayımlanmamış.")} action={tx("Platform yöneticinizden kurulum paketini yayımlamasını isteyin.")} />}
          </Card>
          {!ticket && <p className="text-sm text-text-muted">{tx("Anahtarı oluşturduğunuzda komut burada görünür.")}</p>}
          {ticket && secret && <div className="space-y-3">
            <details className="space-y-2 text-sm">
              <summary className="cursor-pointer font-medium">{tx('Başka yollarla kur')}</summary>
              <div className="space-y-2 pt-2">
                <Field label={tx('Tek kullanımlık kayıt anahtarı')}>{control => <TextArea {...control} readOnly value={secret} rows={2} />}</Field>
                <CopyButton value={secret} label={tx('Kayıt anahtarını kopyala')} />
                {platform === 'Windows' && (distribution?.packages['win-x64-setup']
                  ? <div className="space-y-1">
                    <p><a className="text-primary underline" href={`${server}/api/agent-dist/packages/win-x64-setup`} download>{tx('Kurulum sihirbazını indir (.exe)')}</a></p>
                    <p className="text-text-muted">{tx('Sihirbaz yalnızca yukarıdaki anahtarı ve bilgisayarın adını sorar.')} {tx('Program henüz dijital olarak imzalı olmadığından Windows SmartScreen bir uyarı gösterebilir; “Ek bilgi” ve “Yine de çalıştır” ile devam edin.')}</p>
                  </div>
                  : <ErrorNotice what={tx('Windows kurulum sihirbazı (.exe) yayımlanmamış.')} action={tx('Şimdilik yukarıdaki PowerShell komutunu ya da aşağıdaki script ve zip paketini kullanın.')} />)}
                <p><a className="text-primary underline" href={`${server}/api/agent-dist/${platform === 'Windows' ? 'install.ps1' : 'install-agent.sh'}`} download>{tx('Kurulum scriptini indir')}</a></p>
                <p><a className="text-primary underline" href={`${server}/api/agent-dist/packages/${platform === 'Windows' ? 'win-x64' : 'linux-x64'}`} download>{platform === 'Windows' ? tx('Windows zip paketini indir') : tx('Linux tar.gz paketini indir')}</a></p>
              </div>
            </details>
            <Field label={tx("Tek kullanımlık kurulum komutu")}>{(control) => <TextArea {...control} readOnly rows={4} value={command} className="resize-none break-all font-mono text-sm" />}</Field>
            <CopyButton value={command} label={tx("Kurulum komutunu kopyala")} />
            <p className="text-sm text-text-muted">{tx('Bu anahtar tek kullanımlıktır ve süresi dolar.')} {tx('Anahtarı paylaşmayın; günlüklere, ayar dosyalarına veya komut geçmişine kaydetmeyin. Kapatıldıktan sonra tekrar gösterilmez.')} {tx('Son kullanma:')} {formatDateTime(ticket.expiresAt, tx('Henüz bildirilmedi'))}</p>
          </div>}
          {ticket && <>
            <p role="status" className="rounded-lg border border-border bg-surface px-3 py-2 font-medium text-text">{labels[monitor.phase]}</p>
            {(monitor.phase === 'Expired' || monitor.phase === 'Revoked' || monitor.phase === 'Consumed') && <p className="text-sm text-text-muted">{tx('Kayıt tamamlanmadı. Kayıt anahtarı tek kullanımlıktır ve kurtarılamaz; yeni bir anahtar üretin ve komutu yeniden çalıştırın. Bilgisayarda yarım kalan bir kurulum varsa önce kaldırın.')}</p>}
            {monitor.phase === 'Waiting' && <p className="text-sm text-text-muted">{tx("Komutu hedef bilgisayarda çalıştırın. Kayıt ve ilk bağlantı bekleniyor; bu sayfayı açık bırakın.")}</p>}
          </>}
          <TechnicalDetails items={[]}><p>{tx('Sunucunun TLS sertifikası hedef bilgisayarda güvenilir olmalıdır. Sertifika doğrulamasını asla devre dışı bırakmayın.')}</p></TechnicalDetails>
        </section>}
        {view === 'connect' && <section className="space-y-3">
          <h2 className="font-medium text-text-heading">{tx("Bağlantı")}</h2>
          <p role="status" className="rounded-lg border border-border bg-surface px-3 py-2 font-medium text-text">{labels[monitor.phase]}</p>
          {monitor.phase !== 'Online' && <p className="text-sm text-text-muted">{tx("Kimlik kaydedildi. Yedekleme programının çalıştığını ve sunucuya erişebildiğini kontrol edin; bağlantı bekleniyor.")}</p>}
          {monitor.agent && <>
            <Facts columns={1}><Fact label={tx("Ad")}>{monitor.agent.hostName ?? tx('Bildirilmedi')}</Fact></Facts>
            <TechnicalDetails items={[
              { label: tx('Bilgisayar kimliği'), value: monitor.agent.agentId },
              { label: tx('İşletim sistemi'), value: monitor.agent.osDescription ?? monitor.agent.platform },
              ...(monitor.agent.agentVersion ? [{ label: tx('Yedekleme programı sürümü'), value: monitor.agent.agentVersion }] : []),
            ]} />
          </>}
        </section>}
        {(error || monitor.error) && <ErrorNotice {...(error ?? monitor.error!)} />}
        <div className="flex flex-wrap justify-end gap-2">
          {view === 'install' && <Button variant="outline" disabled={pending} onClick={() => setStep('computer')}>{tx("Geri")}</Button>}
          <Dialog.Close render={<Button variant="outline" />}>{monitor.phase === 'Online' ? tx('Tamam') : tx('Kapat')}</Dialog.Close>
          {monitor.paused && <Button variant="outline" onClick={monitor.retry}>{tx("Takibi tekrar dene")}</Button>}
          {view === 'computer' && <Button type="submit" form="add-agent" disabled={!server || !packageReady || !name.trim()}>{tx("İleri")}</Button>}
          {view === 'install' && <Button disabled={pending || !server || !packageReady || !name.trim()} onClick={() => void create()}>{pending ? tx('Oluşturuluyor…') : ticket ? tx('Yeni kayıt anahtarı oluştur') : tx('Kayıt anahtarı oluştur')}</Button>}
        </div>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
