import { useEffect, useRef, useState } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { useLanguage, tx } from '@/lib/i18n'
import { inputClass, selectClass } from '@/features/platform/styles'
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

/** Mounted only for a tenant administrator while open. Secrets never leave transient state. */
export function AddAgentDialog({ tenantId, onClose, onCreated }: { tenantId: string; onClose: () => void; onCreated?: () => void }) {
  const { language } = useLanguage()
  const client = useQueryClient()
  const [platform, setPlatform] = useState<AgentPlatform>('Windows')
  const [server, setServer] = useState(() => httpsServerOrigin(import.meta.env.VITE_AGENT_PUBLIC_URL ?? '') || suggestedServerOrigin(API_BASE_URL, window.location.origin))
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
  const serverRef = useRef<HTMLInputElement>(null)
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
  const labels = { Waiting: tx('Bekleniyor'), Checking: tx('Token gizlendi; kayıt durumu doğrulanıyor'), Enrolled: tx('Kayıt tamamlandı'), Connecting: tx('Bağlanıyor'), Online: tx('Çevrimiçi'), Expired: tx('Token süresi doldu'), Revoked: tx('Token iptal edildi'), Consumed: tx('Token kullanıldı; Agent bilgisi bulunamadı') }
  return <Dialog.Root open onOpenChange={(open) => { if (!open) close() }}>
    <Dialog.Portal><Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Dialog.Popup initialFocus={serverRef} className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-xl border border-border bg-surface p-6 shadow-xl">
        <Dialog.Title className="text-lg font-semibold text-text-heading">{tx("Agent ekle")}</Dialog.Title>
        <Dialog.Description className="text-sm text-text-muted">{tx("Kayıt token’ı tek kullanımlıktır. Kalıcı Agent kimliği makinede güvenli olarak saklanır; bu ekranda gösterilmez.")}</Dialog.Description>
        <form id="add-agent" className="grid gap-4 sm:grid-cols-2" onSubmit={(event) => { event.preventDefault(); void create() }}>
          <Field label="Platform">{(control) => <select {...control} disabled={pending} value={platform} onChange={event => setPlatform(event.target.value as AgentPlatform)} className={`${selectClass} w-full`}><option>Windows</option><option>Linux</option></select>}</Field>
          <Field label={tx('Agent name')}>{(control) => <input {...control} disabled={pending} value={name} maxLength={200} onChange={event => setName(event.target.value)} className={inputClass} />}</Field>
          <div className="sm:col-span-2"><Field label={tx("Backend HTTPS adresi")} hint={httpsServerOrigin(server) ? undefined : <span className="text-warning">{tx("Agent’ın erişebildiği HTTPS backend origin adresini girin (yalnızca protokol, sunucu ve port). HTTP geliştirme proxy’si güvenli bir Agent adresi belirlemez.")}</span>}>{(control) => <input {...control} ref={serverRef} required type="url" disabled={pending} value={server} placeholder="https://backup.example.com" onChange={event => { serverEdited.current = true; setServer(event.target.value) }} className={inputClass} />}</Field></div>
          <Field label={tx("Geçerlilik süresi (dakika)")}>{(control) => <input {...control} required type="number" min={5} max={10080} step={1} disabled={pending} value={lifetime} onChange={event => setLifetime(event.target.value)} className={inputClass} />}</Field>
        </form>
        <Tabs label={tx('Installation mode')} value={mode} onChange={setMode} idPrefix="agent-install-mode" items={[{ value: 'interactive', label: tx('Guided installation') }, { value: 'automatic', label: tx('Automatic installation') }]} />
        <section role="tabpanel" id={`agent-install-mode-panel-${mode}`} aria-labelledby={`agent-install-mode-tab-${mode}`} className="space-y-2 rounded-lg border border-border bg-surface-2 p-4 text-sm text-text-muted">
          <h2 className="font-medium text-text-heading">{tx('Install and start the agent')}</h2>
          <p>{mode === 'interactive' ? tx('The installer asks for missing values. Paste the enrollment key at its prompt; the command does not contain it.') : tx('The installer downloads, enrolls and starts the service without prompting. Enter an agent name before copying the command.')}</p>
          <p>{platform === 'Windows' ? tx('Open PowerShell as Administrator on the target Windows machine.') : tx('Open a terminal with sudo access on the target Linux machine. The installer creates a systemd service.')}</p>
          <p>{tx('The server TLS certificate must be trusted on the target machine. Never disable certificate verification.')}</p>
          {!distribution && !distributionError && <p>{tx('Checking installer availability…')}</p>}
          {distributionError && <ErrorNotice {...distributionError} />}
          {distribution && !distribution.packages[platform === 'Windows' ? 'win-x64' : 'linux-x64'] && <ErrorNotice {...accessDenied(tx('The installer package is not published for this platform. Contact your administrator.'))} />}
        </section>
        {ticket && <section className="space-y-3 rounded-lg border border-border bg-surface-2 p-4">
          <h2 className="font-medium text-text-heading">{tx("2. Kaydedin ve başlatın")}</h2>
          {secret && <>
            {mode === 'interactive' && <><Field label={tx('One-time enrollment key')}>{control => <textarea {...control} readOnly value={secret} rows={2} className={inputClass} />}</Field><CopyButton value={secret} label={tx('Copy enrollment key')} /></>}
            <Field label={tx("Tek seferlik kurulum komutu")}>{(control) => <textarea {...control} readOnly rows={4} value={distribution?.packages[platform === 'Windows' ? 'win-x64' : 'linux-x64'] ? enrollmentCommands(platform, server, secret, name, mode, language) : ''} className="mt-1 block w-full resize-none break-all rounded-lg border border-border-strong/70 bg-surface p-3 font-mono text-sm text-text" />}</Field>
            <CopyButton value={distribution?.packages[platform === 'Windows' ? 'win-x64' : 'linux-x64'] ? enrollmentCommands(platform, server, secret, name, mode, language) : ''} label={tx("Kurulum komutunu kopyala")} />
            {mode === 'automatic' && !name.trim() && <Notice tone="warning">{tx('Enter an agent name to generate an automatic installation command.')}</Notice>}
            <p className="text-sm text-text-muted">{tx('Do not share the key or save it in logs, configuration files or shell history. It will not be shown again after closing.')} {formatDateTime(ticket.expiresAt, tx('Henüz bildirilmedi'))}</p>
          </>}
          <p role="status" className="rounded-lg border border-border bg-surface px-3 py-2 font-medium text-text">{tx(labels[monitor.phase])}</p>
          {monitor.phase === 'Waiting' && <p className="text-sm text-text-muted">{tx("Komutu hedef makinede çalıştırın. Kayıt ve ilk heartbeat bekleniyor.")}</p>}
          {(monitor.phase === 'Enrolled' || monitor.phase === 'Connecting') && <p className="text-sm text-text-muted">{tx("Kimlik kaydedildi. Agent hizmetinin çalıştığını ve backend’e erişebildiğini kontrol edin; heartbeat bekleniyor.")}</p>}
          {monitor.agent && <Facts><Fact label={tx("Agent ID")}><span className="break-all font-mono text-sm">{monitor.agent.agentId}</span></Fact><Fact label={tx("Ad")}>{monitor.agent.hostName ?? tx('Bildirilmedi')}</Fact><Fact label={tx("İşletim sistemi")}>{monitor.agent.osDescription ?? monitor.agent.platform}</Fact><Fact label={tx("Sürüm")}>{monitor.agent.agentVersion ?? tx('Bildirilmedi')}</Fact><Fact label={tx("Bağlantı")}>{monitor.agent.connectivity === 'online' ? tx('Çevrimiçi') : monitor.agent.connectivity === 'offline' ? tx('Çevrimdışı') : tx('Bilinmiyor')}</Fact></Facts>}
          {monitor.phase === 'Online' && <p className="text-sm font-medium text-success">{tx("Agent çevrimiçi. Kurulum tamamlandı.")}</p>}
        </section>}
        {(error || monitor.error) && <ErrorNotice {...(error ?? monitor.error!)} />}
        <div className="flex flex-wrap justify-end gap-2"><Dialog.Close render={<Button variant="outline" />}>{monitor.phase === 'Online' ? tx('Tamam') : tx('Kapat')}</Dialog.Close>{monitor.paused && <Button variant="outline" onClick={monitor.retry}>{tx("Takibi tekrar dene")}</Button>}{monitor.phase !== 'Online' && <Button type="submit" form="add-agent" disabled={pending || !httpsServerOrigin(server)}>{pending ? tx('Oluşturuluyor…') : ticket ? tx('Yeni token oluştur') : tx('Token oluştur')}</Button>}</div>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
