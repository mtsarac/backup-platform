import { useState } from 'react'
import { History } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { ArtifactDetails } from '@/features/artifacts/artifact-details'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { useBackup, useBackupActions } from '@/features/backups/hooks'
import { canCancelRun, canRetryRun, formatDuration, formatSeconds, formatTimestamp, isActiveRun, progressStageLabel, retryStateLabels, triggerLabel } from '@/features/backups/presentation'
import { ReplicationBadge, ReplicationSection } from '@/features/backups/replication'
import type { BackupAttempt, BackupRun } from '@/features/backups/types'
import { linkClass } from '@/features/platform/styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'

type Tab = 'summary' | 'attempts' | 'replication' | 'timing'

function AttemptProgress({ attempt }: { attempt: BackupAttempt }) {
  const percent = attempt.progressPercent == null ? undefined : Math.min(100, Math.max(0, attempt.progressPercent))
  return <div className="space-y-2">
    <p className="text-sm text-text-muted">İlerleme: {percent == null ? 'Yüzde bildirilmedi' : `%${percent.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`}</p>
    <div role="progressbar" aria-label={`${attempt.number}. deneme ilerlemesi`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="h-2 overflow-hidden rounded-full bg-surface-2">
      {percent != null && <div className="h-full rounded-full bg-primary" style={{ width: `${percent}%` }} />}
    </div>
  </div>
}

export function BackupDetailPage() {
  const { id = '' } = useParams()
  const { tenantId } = useTenant()
  return <BackupDetail key={`${tenantId}:${id}`} id={id} />
}

function BackupDetail({ id }: { id: string }) {
  const { tenantId, canOperate } = useTenant()
  const query = useBackup(id)
  const actions = useBackupActions(id)
  const [notice, setNotice] = useState('')
  const [tab, setTab] = useState<Tab>('summary')
  const [confirmCancel, setConfirmCancel] = useState(false)
  const run = query.data
  const busy = actions.retry.isPending || actions.cancel.isPending

  async function perform(action: 'retry' | 'cancel') {
    setNotice('')
    actions.retry.reset()
    actions.cancel.reset()
    try {
      const response: BackupRun = await actions[action].mutateAsync()
      setNotice(action === 'retry' ? 'Yeni deneme sıraya alındı.' : response.status === 'Cancelled' ? 'Çalıştırma iptal edildi.' : 'İptal isteği iletildi. Ajanın işlemi durdurması bekleniyor.')
      setConfirmCancel(false)
    } catch {
      // The mutation's central error mapping is rendered in the page; never fabricate a state.
    }
  }

  const tabs: { value: Tab; label: string }[] = [
    { value: 'summary', label: 'Özet' },
    { value: 'attempts', label: `Denemeler${run ? ` (${run.attempts.length})` : ''}` },
    ...(run?.replication ? [{ value: 'replication' as const, label: 'Replikasyon' }] : []),
    { value: 'timing', label: 'Zamanlama ve politika' },
  ]
  const latest = run?.attempts.at(-1)

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: 'Backup Run\'lar', to: `/tenants/${tenantId}/backup-runs` }, { label: run?.jobName ?? 'Backup Run' }]}
        title={run?.jobName ?? 'Backup Run'}
        description={run ? `${run.configuration.database} · ${run.agentName ?? 'Agent'}` : undefined}
        meta={run && <><StatusPill status={run.status} /><ReplicationBadge replication={run.replication} /><span className="text-sm text-text-muted">{triggerLabel(run.triggerType)} · {formatTimestamp(run.startedAt ?? run.createdAt)}</span></>}
        actions={run && canOperate && <>
          {canRetryRun(run.status) && <Button disabled={busy} onClick={() => void perform('retry')}>{actions.retry.isPending ? 'Sıraya alınıyor…' : 'Yeniden dene'}</Button>}
          {canCancelRun(run.status) && <Button variant="destructive" disabled={busy} onClick={() => { actions.cancel.reset(); setConfirmCancel(true) }}>{actions.cancel.isPending ? 'İletiliyor…' : 'İptal et'}</Button>}
        </>}
      />
      <QueryState isPending={query.isPending} error={query.error} empty={!query.isPending && !run}>
        {run && <>
          <ConfirmDialog open={confirmCancel && canOperate && canCancelRun(run.status)} onClose={() => setConfirmCancel(false)} title="Bu çalıştırma iptal edilsin mi?" confirmLabel="İptali onayla"
            description="Agent'a iptal isteği iletilir. Agent işlemi durdurana kadar çalıştırma iptal edilmiş sayılmaz; yarım kalan yedek kullanılamaz."
            pending={actions.cancel.isPending} error={actions.cancel.error ? errorMessage(actions.cancel.error) : undefined} onConfirm={() => void perform('cancel')} />
          {notice && run.status !== 'Succeeded' && !(run.status === 'Cancelled' && notice.startsWith('İptal isteği')) && <Notice tone="success">{notice}</Notice>}
          {actions.retry.error && <Notice tone="error">{errorMessage(actions.retry.error)}</Notice>}
          {actions.cancel.error && !confirmCancel && <Notice tone="error">{errorMessage(actions.cancel.error)}</Notice>}
          <Tabs label="Backup Run bölümleri" idPrefix="run" value={tab} onChange={setTab} items={tabs} />
          <div role="tabpanel" id={`run-panel-${tab}`} aria-labelledby={`run-tab-${tab}`} className="space-y-6">
            {tab === 'summary' && <>
              {(run.status === 'Failed' || run.status === 'AgentLost') && <Section tone="danger" title="Çalıştırma başarısız oldu">
                <div className="space-y-1 text-sm">
                  <p className="font-medium text-error">{failureMessage(run.reasonCode ?? run.retry.lastFailureCode)}</p>
                  {run.reasonCode && <p className="text-text-muted">Hata kodu: {run.reasonCode}</p>}
                  <p className="text-text">{run.retry.lastFailureRetryable === false ? 'Bu hata otomatik olarak yeniden denenmez. Elle yeniden deneme, kalan deneme hakkı ve işin çakışma politikasına bağlıdır.' : 'Otomatik yeniden deneme sona erdi. Elle yeniden deneme, kalan deneme hakkı ve işin çakışma politikasına bağlıdır.'}</p>
                </div>
              </Section>}
              {run.status === 'RetryPending' && <Notice tone="warning">Otomatik yeniden deneme bekleniyor. Sonraki deneme: {formatTimestamp(run.retry.nextAttemptAt)}. {failureMessage(run.retry.lastFailureCode)}</Notice>}
              {run.status === 'CancellationRequested' && <Notice tone="warning">İptal istendi; çalıştırma henüz iptal edilmiş değildir. Ajan sonucu bildirdiğinde durum güncellenir.</Notice>}
              {isActiveRun(run.status) && latest && <Section title="Güncel deneme" description={`${latest.number}. deneme · ${progressStageLabel(latest.progressStage)}`}><AttemptProgress attempt={latest} /></Section>}
              <Section title="Çalıştırma özeti">
                <Facts columns={3}>
                  <Fact label="Veritabanı">{run.configuration.database}</Fact>
                  <Fact label="Agent">{run.agentName ?? '—'}</Fact>
                  <Fact label="Tetikleyici">{triggerLabel(run.triggerType)}</Fact>
                  <Fact label="Yedek türü">{run.backupType}</Fact>
                  <Fact label="Veritabanı motoru">{run.configuration.engine}</Fact>
                  <Fact label="Deneme sayısı">{run.currentAttemptNumber} / {run.retry.maxAttempts}</Fact>
                  <Fact label="Başlangıç">{formatTimestamp(run.startedAt)}</Fact>
                  <Fact label="Bitiş">{formatTimestamp(run.completedAt)}</Fact>
                  <Fact label="Süre">{formatDuration(run.startedAt, run.completedAt)}</Fact>
                </Facts>
              </Section>
            </>}
            {tab === 'attempts' && (run.attempts.length === 0
              ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={History} title="Henüz deneme oluşturulmadı." /></div>
              : run.attempts.map((attempt) => <Section key={attempt.id} title={`${attempt.number}. deneme · ${triggerLabel(attempt.trigger)}`} actions={<><StatusPill status={attempt.status} /><ReplicationBadge replication={attempt.replication} /></>}>
                <div className="space-y-4">
                  <Facts columns={3}>
                    <Fact label="Oluşturulma">{formatTimestamp(attempt.createdAt)}</Fact>
                    <Fact label="Başlangıç">{formatTimestamp(attempt.startedAt)}</Fact>
                    <Fact label="Bitiş">{formatTimestamp(attempt.completedAt)}</Fact>
                    <Fact label="Süre">{formatDuration(attempt.startedAt, attempt.completedAt)}</Fact>
                    <Fact label="Aşama">{progressStageLabel(attempt.progressStage)}</Fact>
                    <Fact label="Son ilerleme">{formatTimestamp(attempt.progressAt)}</Fact>
                  </Facts>
                  <AttemptProgress attempt={attempt} />
                  {attempt.reasonCode && <div className="text-sm"><p className="text-error">{failureMessage(attempt.reasonCode)}</p><p className="mt-1 text-text-muted">Hata kodu: {attempt.reasonCode}</p>{attempt.retryable != null && <p className="mt-1 text-text-muted">{attempt.retryable ? 'Otomatik yeniden denemeye uygun (politika sınırları içinde).' : 'Otomatik yeniden denemeye uygun değil; elle yeniden deneme ayrı bir işlemdir.'}</p>}</div>}
                  <section aria-label={`${attempt.number}. deneme çıktıları`} className="space-y-3 border-t border-border pt-4">
                    <h3 className="text-sm font-medium text-text-heading">Yedek çıktıları</h3>
                    {attempt.artifacts.length === 0 ? <p className="text-sm text-text-muted">Bu denemede kayıtlı yedek çıktısı yok.</p> : attempt.artifacts.map((artifact) => <div key={artifact.id} className="space-y-2"><ArtifactDetails artifact={{ ...artifact, jobName: artifact.jobName ?? run.jobName, agentName: artifact.agentName ?? run.agentName, databaseName: artifact.databaseName ?? run.configuration.database }} /><Link className={`text-sm ${linkClass}`} to={`/tenants/${tenantId}/artifacts/${artifact.id}`}>Yedek çıktısını aç</Link></div>)}
                  </section>
                </div>
              </Section>))}
            {tab === 'replication' && <ReplicationSection replication={run.replication} />}
            {tab === 'timing' && <>
              <Section title="Zaman çizelgesi">
                <Facts columns={3}>
                  <Fact label="Oluşturulma">{formatTimestamp(run.createdAt)}</Fact>
                  <Fact label="Planlanan zaman">{formatTimestamp(run.scheduledFor)}</Fact>
                  <Fact label="Planlama gecikmesi">{run.scheduleDelaySeconds == null ? '—' : formatSeconds(run.scheduleDelaySeconds)}</Fact>
                  <Fact label="Ajan alım son zamanı">{formatTimestamp(run.claimDeadline)}</Fact>
                  <Fact label="Başlangıç">{formatTimestamp(run.startedAt)}</Fact>
                  <Fact label="İptal isteği">{formatTimestamp(run.cancellationRequestedAt)}</Fact>
                  <Fact label="Bitiş">{formatTimestamp(run.completedAt)}</Fact>
                  <Fact label="Süre">{formatDuration(run.startedAt, run.completedAt)}</Fact>
                </Facts>
              </Section>
              <Section title="Yeniden deneme ve saklama politikası">
                <Facts columns={3}>
                  <Fact label="Yeniden deneme durumu">{retryStateLabels[run.retry.state] ?? run.retry.state}</Fact>
                  <Fact label="Sonraki deneme">{formatTimestamp(run.retry.nextAttemptAt)}</Fact>
                  <Fact label="Çakışma politikası">{run.retry.overlapPolicy === 'Skip' ? 'Aktif iş varsa atla' : 'Sıraya al'}</Fact>
                  <Fact label="Yeniden deneme ilk gecikmesi">{formatSeconds(run.retry.policy.initialDelaySeconds)}</Fact>
                  <Fact label="Yeniden deneme üst gecikmesi">{formatSeconds(run.retry.policy.maxDelaySeconds)}</Fact>
                  <Fact label="Geri çekilme">{`${run.retry.policy.backoff} · Jitter ${run.retry.policy.jitter}`}</Fact>
                  <Fact label="Saklama">{run.retention?.enabled ? formatSeconds(run.retention.keepForSeconds) : 'Otomatik silme kapalı'}</Fact>
                  <Fact label="Silme bekleme süresi">{run.retention?.enabled ? formatSeconds(run.retention.deleteGracePeriodSeconds) : '—'}</Fact>
                </Facts>
              </Section>
            </>}
          </div>
        </>}
      </QueryState>
    </div>
  )
}
