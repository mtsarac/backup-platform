import { tx, useLanguage } from '@/lib/i18n'
import { Select } from '@/components/ui/text-input'
import { restorePath } from '@/lib/routes'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { DataList } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { restoreIneligibility } from '@/features/artifacts/restore-eligibility'
import type { BackupArtifact } from '@/features/artifacts/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useRestores, useRestoreTargets, useStartRestoreVerification } from './hooks'
import { verificationCleanupText } from './presentation'
import { formatDateTime, formatDuration } from '@/lib/format'
import { useIdempotencyKey } from '@/lib/idempotency-key'
import { errorMessage } from '@/lib/api/failure-messages'
import { statusLabel } from '@/lib/status'

export function ArtifactVerificationSection({ tenantId, artifact }: { tenantId: string; artifact: BackupArtifact }) {
  useLanguage()
  const { canOperate } = useTenant()
  const history = useRestores({ purpose: 'Verification', artifactId: artifact.id, pageSize: 10 })
  if (restoreIneligibility(artifact)) return null
  const rows = history.data?.items ?? []
  return <Section title={tx("Geri yüklenebilir mi?")} description={tx("Backup'ın gerçekten geri yüklenebildiğini geçici, izole bir Database üzerinde kanıtlayın.")}
>
    <div className="space-y-4">
      {history.error ? <ErrorNotice title={tx("Restore testleri yüklenemedi")} error={history.error} scope="restore" onRetry={() => void history.refetch()} />
        : rows.length === 0 && !history.isPending && <p className="text-sm text-text-muted">{tx("Henüz test edilmedi.")}</p>}
      {canOperate && <RestoreTestStarter tenantId={tenantId} artifact={artifact} />}
      {rows.length > 0 && <DataList caption={tx("Son restore testleri")} emptyMessage={tx("Henüz test edilmedi.")} rows={rows} keyExtractor={row => row.id} columns={[
        { key: 'outcome', header: tx('Sonuç'), card: 'status', render: row => <>{row.verificationOutcome ? <StatusPill domain="verification" status={row.verificationOutcome} /> : <StatusPill status={row.status} />}{row.cleanup && row.cleanup.state !== 'Completed' && <p className="mt-1 text-xs text-text-muted">{verificationCleanupText(row.cleanup)}</p>}</> },
        { key: 'started', header: tx('Başlangıç'), render: row => formatDateTime(row.startedAt ?? row.createdAt) },
        { key: 'duration', header: tx('Süre'), render: row => row.completedAt ? formatDuration(row.startedAt, row.completedAt) : '—' },
        { key: 'agent', header: tx('Agent'), card: 'hidden', render: row => <span className="break-all font-mono text-xs">{row.agentId}</span> },
        { key: 'open', header: tx('Ayrıntı'), card: 'actions', render: row => <Link className="text-primary hover:underline" to={restorePath(tenantId, row.id)}>{tx("Aç")}</Link> },
      ]} />}
    </div>
  </Section>
}

/** One button: the test runs on the backup's own server and computer; a computer is asked only when more than one can run it. */
export function RestoreTestStarter({ tenantId, artifact, label, onStarted }: { tenantId: string; artifact: BackupArtifact; label?: string; onStarted?: (restoreId: string) => void }) {
  useLanguage()
  const navigate = useNavigate()
  const targets = useRestoreTargets(artifact.id)
  const start = useStartRestoreVerification()
  const idempotencyKey = useIdempotencyKey()
  const [agentId, setAgentId] = useState('')
  const [error, setError] = useState<unknown>(null)
  if (targets.isPending) return <p className="text-sm text-text-muted">{tx("Hedefler yükleniyor…")}</p>
  if (targets.error || !targets.data) return <ErrorNotice title={tx("Hedefler yüklenemedi")} error={targets.error} scope="restore" onRetry={() => void targets.refetch()} />
  const agents = targets.data.agents.filter((agent) => agent.canVerify && agent.sourceEligible)
  const target = targets.data.targets.find((item) => item.jobId === artifact.jobId)?.target
  const selectedAgent = agents.find((agent) => agent.agentId === agentId)?.agentId ?? agents[0]?.agentId ?? ''
  async function submit() {
    if (!target || !selectedAgent || start.isPending) return
    const request = { agentId: selectedAgent, target: { ...target }, options: { verifyChecksum: true, verifyBackup: true } }
    setError(null)
    try {
      const restore = await start.mutateAsync({ artifactId: artifact.id, idempotencyKey: idempotencyKey.keyFor(request), request })
      idempotencyKey.settle()
      if (onStarted) onStarted(restore.id)
      else navigate(restorePath(tenantId, restore.id))
    } catch (caught) {
      idempotencyKey.settle(caught)
      setError(caught)
    }
  }
  if (agents.length === 0) return <Notice tone="warning">{tx("Bu yedeği test edebilecek uygun bir bilgisayar yok.")}</Notice>
  if (!target) return <Notice tone="warning">{tx("Yedeğin alındığı sunucu bulunamadığı için geri yükleme testi başlatılamıyor.")}</Notice>
  return <div className="space-y-3">
    <div className="flex flex-wrap items-end gap-3">
      {agents.length > 1 && <div><label htmlFor="verify-agent" className="block text-sm font-medium">{tx("Yedekleme bilgisayarı")}</label>
        <Select id="verify-agent" value={selectedAgent} onChange={(event) => setAgentId(event.target.value)}>{agents.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? agent.agentId} ({statusLabel('agent', agent.connectivity)})</option>)}</Select></div>}
      <Button variant="outline" disabled={start.isPending} onClick={() => void submit()}>{start.isPending ? tx('Başlatılıyor…') : label ?? tx('Restore testi başlat')}</Button>
    </div>
    {error != null && <p role="alert" className="text-sm text-error">{errorMessage(error, 'restore')}</p>}
  </div>
}
