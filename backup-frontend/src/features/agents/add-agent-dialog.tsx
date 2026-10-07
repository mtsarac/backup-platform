import { useEffect, useRef, useState } from 'react'
import { Dialog } from '@base-ui/react/dialog'
import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { API_BASE_URL } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import { createEnrollmentToken, getEnrollmentTokenStatus, revokeEnrollmentToken } from './api'
import { agentQueryKeys } from './hooks'
import { agentDate } from './format'
import { enrollmentCommands, httpsServerOrigin, suggestedServerOrigin } from './enrollment-commands'
import type { AgentPlatform } from './enrollment-commands'
import { useEnrollmentMonitor } from './use-enrollment-monitor'

/** Mounted only for a tenant administrator while open. Secrets never leave transient state. */
export function AddAgentDialog({ tenantId, onClose }: { tenantId: string; onClose: () => void }) {
  const client = useQueryClient()
  const [platform, setPlatform] = useState<AgentPlatform>('Windows')
  const [server, setServer] = useState(() => suggestedServerOrigin(API_BASE_URL, window.location.origin))
  const [name, setName] = useState('')
  const [lifetime, setLifetime] = useState('60')
  const [ticket, setTicket] = useState<{ tokenId: string; expiresAt: string } | null>(null)
  const [secret, setSecret] = useState('')
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const request = useRef<AbortController | null>(null)
  const serverRef = useRef<HTMLInputElement>(null)
  const monitor = useEnrollmentMonitor(tenantId, ticket, () => setSecret(''))
  useEffect(() => () => { request.current?.abort() }, [])
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
    setPending(true); setError(''); setSecret('')
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
    } catch (cause) { if (!controller.signal.aborted) setError(errorMessage(cause)) }
    finally { if (!controller.signal.aborted) setPending(false) }
  }
  const labels = { Waiting: 'Bekleniyor', Checking: 'Token gizlendi; kayıt durumu doğrulanıyor', Enrolled: 'Kayıt tamamlandı', Connecting: 'Bağlanıyor', Online: 'Çevrimiçi', Expired: 'Token süresi doldu', Revoked: 'Token iptal edildi', Consumed: 'Token kullanıldı; Agent bilgisi bulunamadı' }
  return <Dialog.Root open onOpenChange={(open) => { if (!open) close() }}>
    <Dialog.Portal><Dialog.Backdrop className="fixed inset-0 z-40 bg-black/50" />
      <Dialog.Popup initialFocus={serverRef} className="fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2 -translate-y-1/2 space-y-4 overflow-y-auto rounded-xl border border-border bg-surface p-6 shadow-xl">
        <Dialog.Title className="text-lg font-semibold text-text-heading">Agent ekle</Dialog.Title>
        <Dialog.Description className="text-sm text-text-muted">Kayıt token’ı tek kullanımlıktır. Kalıcı Agent kimliği makinede güvenli olarak saklanır; bu ekranda gösterilmez.</Dialog.Description>
        <form id="add-agent" className="grid gap-3 sm:grid-cols-2" onSubmit={(event) => { event.preventDefault(); void create() }}>
          <label className="text-sm text-text-muted">Platform<select disabled={pending || Boolean(ticket)} value={platform} onChange={event => setPlatform(event.target.value as AgentPlatform)} className="mt-1 block w-full rounded-lg border border-border bg-surface-2 p-2 text-text"><option>Windows</option><option>Linux</option></select></label>
          <label className="text-sm text-text-muted">Agent adı (isteğe bağlı)<input disabled={pending || Boolean(ticket)} value={name} maxLength={200} onChange={event => setName(event.target.value)} className="mt-1 block w-full rounded-lg border border-border bg-surface-2 p-2 text-text" /></label>
          <label className="text-sm text-text-muted sm:col-span-2">Backend HTTPS adresi<input ref={serverRef} required type="url" disabled={pending || Boolean(ticket)} value={server} placeholder="https://backup.example.com" onChange={event => setServer(event.target.value)} className="mt-1 block w-full rounded-lg border border-border bg-surface-2 p-2 text-text" /></label>
          {!httpsServerOrigin(server) && <p className="text-sm text-warning sm:col-span-2">Agent’ın erişebildiği HTTPS backend origin adresini girin (yalnızca protokol, sunucu ve port). HTTP geliştirme proxy’si güvenli bir Agent adresi belirlemez.</p>}
          <label className="text-sm text-text-muted">Geçerlilik süresi (dakika)<input required type="number" min={5} max={10080} step={1} disabled={pending} value={lifetime} onChange={event => setLifetime(event.target.value)} className="mt-1 block w-full rounded-lg border border-border bg-surface-2 p-2 text-text" /></label>
        </form>
        <section className="space-y-2 rounded-lg border border-border bg-surface-2 p-3 text-sm text-text-muted">
          <h2 className="font-medium text-text-heading">1. Yayınlanmış Agent klasörünü hazırlayın</h2>
          <p>Hedef platform için yayınlanmış Agent dosyalarını yöneticinizden alın veya repository’deki yayınlama adımlarını kullanın. Bu uygulama henüz bir indirme bağlantısı sağlamıyor.</p>
          <p>{platform === 'Windows' ? 'Klasörde BackupPlatform.Agent.Host.exe ve deploy/windows/Install-BackupAgentService.ps1 bulunmalıdır. PowerShell’i yönetici olarak bu klasörde açın. Enrollment komutu mevcut installer ile NT SERVICE\\BackupAgent kimliğiyle hizmeti kurar ve başlatır.' : 'Klasörde çalıştırılabilir BackupPlatform.Agent.Host bulunmalıdır. Terminali bu klasörde açın. Her iki komutu aynı Unix hesabıyla çalıştırın. Kimlik $HOME/.local/share/backup-agent altında yalnızca sahibinin erişebildiği dizinde (0700), dosyalarda (0600) saklanır. İlk komut kayıt yapar; ikinci komut Agent’ı ön planda başlatır. Terminal açık kalmalıdır; bu adımlar systemd hizmeti kurmaz.'}</p>
          <p>Backend TLS sertifikası hedef makinede güvenilir olmalıdır. Sertifika doğrulamasını devre dışı bırakmayın.</p>
        </section>
        {ticket && <section className="space-y-3">
          <h2 className="font-medium text-text-heading">2. Kaydedin ve başlatın</h2>
          {secret && <><label className="block text-sm text-text-muted">Tek seferlik kurulum komutu<textarea readOnly rows={platform === 'Windows' ? 4 : 5} value={enrollmentCommands(platform, server, secret, name)} className="mt-1 block w-full resize-none rounded-lg border border-border bg-surface-2 p-3 font-mono text-sm text-text" /></label><p className="text-xs text-text-muted">Token içeren komutu paylaşmayın, log/config dosyasına kaydetmeyin. Komut geçmişinde saklanmasını önleyin. Son kullanma: {agentDate(ticket.expiresAt)}. Kapatınca tekrar görüntülenemez.</p></>}
          <p role="status" className="font-medium text-text">{labels[monitor.phase]}</p>
          {monitor.phase === 'Waiting' && <p className="text-sm text-text-muted">Komutu hedef makinede çalıştırın. Kayıt ve ilk heartbeat bekleniyor.</p>}
          {(monitor.phase === 'Enrolled' || monitor.phase === 'Connecting') && <p className="text-sm text-text-muted">Kimlik kaydedildi. Agent hizmetinin çalıştığını ve backend’e erişebildiğini kontrol edin; heartbeat bekleniyor.</p>}
          {monitor.agent && <dl className="grid grid-cols-2 gap-2 break-all text-sm"><dt>Agent ID</dt><dd>{monitor.agent.agentId}</dd><dt>Ad</dt><dd>{monitor.agent.hostName ?? 'Bildirilmedi'}</dd><dt>İşletim sistemi</dt><dd>{monitor.agent.osDescription ?? monitor.agent.platform}</dd><dt>Sürüm</dt><dd>{monitor.agent.agentVersion ?? 'Bildirilmedi'}</dd><dt>Bağlantı</dt><dd>{monitor.agent.connectivity}</dd></dl>}
          {monitor.phase === 'Online' && <p className="text-sm text-success">Agent çevrimiçi. Kurulum tamamlandı.</p>}
        </section>}
        {(error || monitor.error) && <p role="alert" className="text-sm text-error">{error || monitor.error}</p>}
        <div className="flex flex-wrap justify-end gap-2"><Dialog.Close render={<Button variant="outline" />}>{monitor.phase === 'Online' ? 'Tamam' : 'Kapat'}</Dialog.Close>{monitor.paused && <Button variant="outline" onClick={monitor.retry}>Takibi tekrar dene</Button>}{monitor.phase !== 'Online' && <Button type="submit" form="add-agent" disabled={pending || !httpsServerOrigin(server)}>{pending ? 'Oluşturuluyor…' : ticket ? 'Yeni token oluştur' : 'Token oluştur'}</Button>}</div>
      </Dialog.Popup>
    </Dialog.Portal>
  </Dialog.Root>
}
