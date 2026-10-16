import { tx, useLanguage } from '@/lib/i18n'
import { useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { StatusPill } from '@/components/data/status-pill'
import { restoreIneligibility } from '@/features/artifacts/restore-eligibility'
import type { BackupArtifact } from '@/features/artifacts/types'
import { formatDuration, formatTimestamp } from '@/features/backups/presentation'
import { inputClass, selectClass } from '@/features/platform/styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { StoredLoginPanel } from './stored-login-panel'
import { useRestores, useRestoreTargets, useStartRestoreVerification } from './hooks'
import { restoreErrorMessage, verificationCleanupText, verificationOutcomeLabels } from './presentation'
import type { RestoreTarget, RestoreView, StartRestoreVerificationVariables } from './types'

const outcomeTone = { Restored: 'success', Failed: 'error', NotRun: 'neutral' } as const

/** Outcome of a verification; an unfinished one shows its operation status instead. */
export function VerificationOutcomeBadge({ restore }: { restore: Pick<RestoreView, 'status' | 'verificationOutcome'> }) {
  useLanguage()
  return restore.verificationOutcome
    ? <Badge tone={outcomeTone[restore.verificationOutcome]} dot>{verificationOutcomeLabels[restore.verificationOutcome]}</Badge>
    : <StatusPill status={restore.status} />
}

export function ArtifactVerificationSection({ tenantId, artifact }: { tenantId: string; artifact: BackupArtifact }) {
  useLanguage()
  const { canOperate } = useTenant()
  const [formOpen, setFormOpen] = useState(false)
  const history = useRestores({ purpose: 'Verification', artifactId: artifact.id, pageSize: 10 })
  if (restoreIneligibility(artifact)) return null
  const rows = history.data?.items ?? []
  const latest = rows[0]
  return <Section title={tx("Restore doğrulaması")} description={tx("Backup'ın gerçekten geri yüklenebildiğini geçici, izole bir Database üzerinde kanıtlayın.")}
    actions={canOperate && !formOpen && <Button variant="outline" onClick={() => setFormOpen(true)}>{tx("Restore testi başlat")}</Button>}>
    <div className="space-y-4">
      {history.error ? <Notice tone="error">{tx("Restore testleri yüklenemedi:")}{' '}{restoreErrorMessage(history.error)}</Notice>
        : latest ? <p className="flex flex-wrap items-center gap-2 text-sm"><VerificationOutcomeBadge restore={latest} /><span>{formatTimestamp(latest.startedAt ?? latest.createdAt)}</span>{latest.completedAt && <span className="text-text-muted">{tx("· Süre:")}{' '}{formatDuration(latest.startedAt, latest.completedAt)}</span>}</p>
          : !history.isPending && <p className="text-sm text-text-muted">{tx("Henüz test edilmedi.")}</p>}
      {formOpen && canOperate && <VerificationForm tenantId={tenantId} artifactId={artifact.id} onClose={() => setFormOpen(false)} />}
      {rows.length > 0 && <div className="overflow-x-auto"><table className="w-full text-left text-sm">
        <caption className="sr-only">{tx("Son restore testleri")}</caption>
        <thead><tr className="text-text-muted"><th className="py-2 pr-4 font-medium">{tx("Sonuç")}</th><th className="py-2 pr-4 font-medium">{tx("Başlangıç")}</th><th className="py-2 pr-4 font-medium">{tx("Süre")}</th><th className="py-2 pr-4 font-medium">{tx("Agent")}</th><th className="py-2 font-medium">{tx("Ayrıntı")}</th></tr></thead>
        <tbody className="divide-y divide-border">{rows.map((row) => <tr key={row.id}>
          <td className="py-2 pr-4"><VerificationOutcomeBadge restore={row} />{row.cleanup && row.cleanup.state !== 'Completed' && <p className="mt-1 text-xs text-text-muted">{verificationCleanupText(row.cleanup)}</p>}</td>
          <td className="py-2 pr-4">{formatTimestamp(row.startedAt ?? row.createdAt)}</td>
          <td className="py-2 pr-4">{row.completedAt ? formatDuration(row.startedAt, row.completedAt) : '—'}</td>
          <td className="py-2 pr-4 break-all font-mono text-xs">{row.agentId}</td>
          <td className="py-2"><Link className="text-primary hover:underline" to={`/tenants/${tenantId}/restores/${row.id}`}>{tx("Aç")}</Link></td>
        </tr>)}</tbody>
      </table></div>}
    </div>
  </Section>
}

function VerificationForm({ tenantId, artifactId, onClose }: { tenantId: string; artifactId: string; onClose: () => void }) {
  useLanguage()
  const navigate = useNavigate()
  const targets = useRestoreTargets(artifactId)
  const start = useStartRestoreVerification()
  const attempt = useRef<{ fingerprint: string; variables: StartRestoreVerificationVariables } | null>(null)
  const [agentId, setAgentId] = useState('')
  const [targetIndex, setTargetIndex] = useState(0)
  const [custom, setCustom] = useState<RestoreTarget>({ host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false })
  const [error, setError] = useState<unknown>(null)
  if (targets.isPending) return <p className="text-sm text-text-muted">{tx("Hedefler yükleniyor…")}</p>
  if (targets.error || !targets.data) return <Notice tone="error">{tx("Hedefler yüklenemedi:")}{' '}{restoreErrorMessage(targets.error)}</Notice>
  const agents = targets.data.agents.filter((agent) => agent.canVerify && agent.sourceEligible)
  const configured = targets.data.targets
  const useConfigured = targetIndex >= 0 && configured[targetIndex] !== undefined
  const target = useConfigured ? configured[targetIndex].target : custom
  const selectedAgent = agentId || agents[0]?.agentId || ''
  const valid = Boolean(selectedAgent) && Boolean(target.host)
  async function submit() {
    if (!valid || start.isPending) return
    const fingerprint = JSON.stringify([selectedAgent, target])
    // A retry after a transport/5xx failure keeps its Idempotency-Key; any edit gets a new one.
    const next = attempt.current?.fingerprint === fingerprint && error != null ? attempt.current : {
      fingerprint,
      variables: { artifactId, idempotencyKey: crypto.randomUUID(), request: { agentId: selectedAgent, target: { ...target }, options: { verifyChecksum: true, verifyBackup: true } } },
    }
    attempt.current = next
    setError(null)
    try {
      const restore = await start.mutateAsync(next.variables)
      navigate(`/tenants/${tenantId}/restores/${restore.id}`)
    } catch (caught) {
      setError(caught)
    }
  }
  return <form className="space-y-4 rounded-lg border border-border bg-surface-2 p-4" onSubmit={(event) => { event.preventDefault(); void submit() }}>
    <Notice tone="info">{tx("Test, seçilen sunucuda geçici ve izole bir Database'e geri yükler ve işlem sonunda bu Database'i siler. Production Database değiştirilmez.")}</Notice>
    {agents.length === 0 ? <Notice tone="warning">{tx("Bu backup'ı test edebilecek uygun bir Agent yok.")}</Notice> : <>
      <div><label htmlFor="verify-agent" className="block text-sm font-medium">{tx("Agent")}</label>
        <select id="verify-agent" className={`${selectClass} w-full`} value={selectedAgent} onChange={(event) => setAgentId(event.target.value)}>{agents.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? agent.agentId} ({tx(agent.connectivity)})</option>)}</select></div>
      <div><label htmlFor="verify-target" className="block text-sm font-medium">{tx("Hedef SQL Server")}</label>
        <select id="verify-target" className={`${selectClass} w-full`} value={useConfigured ? targetIndex : -1} onChange={(event) => setTargetIndex(Number(event.target.value))}>
          {configured.map((item, index) => <option key={`${item.jobId}-${index}`} value={index}>{item.jobName} · {item.target.host}{item.target.port ? `:${item.target.port}` : ''}</option>)}
          <option value={-1}>{tx("Özel Bağlantı")}</option></select></div>
      {!useConfigured && <div className="grid gap-3 sm:grid-cols-2">
        <div><label htmlFor="verify-host" className="block text-sm font-medium">{tx("Host / Instance")}</label><input id="verify-host" className={inputClass} value={custom.host} onChange={(event) => setCustom({ ...custom, host: event.target.value })} placeholder={tx("localhost veya SQLSERVER")} /></div>
        <div><label htmlFor="verify-port" className="block text-sm font-medium">{tx("Port (İsteğe bağlı)")}</label><input id="verify-port" type="number" className={inputClass} value={custom.port ?? ''} onChange={(event) => setCustom({ ...custom, port: event.target.value ? Number(event.target.value) : null })} placeholder="1433" /></div>
        {!custom.integratedSecurity && <StoredLoginPanel tenantId={tenantId} agentId={selectedAgent} idPrefix="verify-login" reference={custom.databaseCredentialReference} onStored={(reference) => setCustom({ ...custom, databaseCredentialReference: reference })} />}
        <div className="flex flex-wrap items-center gap-4 text-sm sm:col-span-2">
          <label className="flex items-center gap-2"><input type="checkbox" checked={custom.integratedSecurity} onChange={(event) => setCustom({ ...custom, integratedSecurity: event.target.checked, databaseCredentialReference: null })} /><span>{tx("Windows kimlik doğrulaması")}</span></label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={custom.encrypt} onChange={(event) => setCustom({ ...custom, encrypt: event.target.checked })} /><span>{tx("Bağlantıyı Şifrele")}</span></label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={custom.trustServerCertificate} onChange={(event) => setCustom({ ...custom, trustServerCertificate: event.target.checked })} /><span>{tx("Sunucu Sertifikasına Güven")}</span></label>
        </div>
      </div>}
    </>}
    {error != null && <p role="alert" className="text-sm text-error">{restoreErrorMessage(error)}</p>}
    <div className="flex justify-end gap-2">
      <Button type="button" variant="outline" disabled={start.isPending} onClick={onClose}>{tx("Vazgeç")}</Button>
      <Button type="submit" disabled={!valid || start.isPending}>{start.isPending ? tx('Başlatılıyor…') : tx('Testi başlat')}</Button>
    </div>
  </form>
}
