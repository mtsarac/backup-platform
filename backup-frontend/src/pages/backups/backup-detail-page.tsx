import { currentLocale, tx, useLanguage } from '@/lib/i18n'
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
import { SizeAnomalyBadge, SizeAnomalyComparison } from '@/features/backups/size-anomaly'
import type { BackupAttempt, BackupRun } from '@/features/backups/types'
import { linkClass } from '@/features/platform/styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'

type Tab = 'summary' | 'attempts' | 'replication' | 'timing'

function AttemptProgress({ attempt }: { attempt: BackupAttempt }) {
  useLanguage()
  const percent = attempt.progressPercent == null ? undefined : Math.min(100, Math.max(0, attempt.progressPercent))
  return <div className="space-y-2">
    <p className="text-sm text-text-muted">{tx("İlerleme:")}{' '}{percent == null ? tx('Yüzde bildirilmedi') : tx('%{0}', { '0': percent.toLocaleString(currentLocale(), { maximumFractionDigits: 1 }) })}</p>
    <div role="progressbar" aria-label={tx("{0}. deneme ilerlemesi", { "0": attempt.number })} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="h-2 overflow-hidden rounded-full bg-surface-2">
      {percent != null && <div className="h-full rounded-full bg-primary" style={{ width: `${percent}%` }} />}
    </div>
  </div>
}

export function BackupDetailPage() {
  useLanguage()
  const { id = '' } = useParams()
  const { tenantId } = useTenant()
  return <BackupDetail key={`${tenantId}:${id}`} id={id} />
}

function BackupDetail({ id }: { id: string }) {
  useLanguage()
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
      setNotice(action === 'retry' ? tx('Yeni deneme sıraya alındı.') : response.status === 'Cancelled' ? tx('Çalıştırma iptal edildi.') : tx('İptal isteği iletildi. Ajanın işlemi durdurması bekleniyor.'))
      setConfirmCancel(false)
    } catch {
      // The mutation's central error mapping is rendered in the page; never fabricate a state.
    }
  }

  const tabs: { value: Tab; label: string }[] = [
    { value: 'summary', label: tx('Özet') },
    { value: 'attempts', label: `Denemeler${run ? ` (${run.attempts.length})` : ''}` },
    ...(run?.replication ? [{ value: 'replication' as const, label: tx('Replikasyon') }] : []),
    { value: 'timing', label: tx('Zamanlama ve politika') },
  ]
  const latest = run?.attempts.at(-1)

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: tx('Backup Run\'lar'), to: `/tenants/${tenantId}/backup-runs` }, { label: run?.jobName ?? tx('Backup Run') }]}
        title={run?.jobName ?? tx('Backup Run')}
        description={run ? `${run.configuration.database} · ${run.agentName ?? 'Agent'}` : undefined}
        meta={run && <><StatusPill status={run.status} /><ReplicationBadge replication={run.replication} /><SizeAnomalyBadge run={run} /><span className="text-sm text-text-muted">{triggerLabel(run.triggerType)} · {formatTimestamp(run.startedAt ?? run.createdAt)}</span></>}
        actions={run && canOperate && <>
          {canRetryRun(run.status) && <Button disabled={busy} onClick={() => void perform('retry')}>{actions.retry.isPending ? tx('Sıraya alınıyor…') : tx('Yeniden dene')}</Button>}
          {canCancelRun(run.status) && <Button variant="destructive" disabled={busy} onClick={() => { actions.cancel.reset(); setConfirmCancel(true) }}>{actions.cancel.isPending ? tx('İletiliyor…') : tx('İptal et')}</Button>}
        </>}
      />
      <QueryState isPending={query.isPending} error={query.error} empty={!query.isPending && !run}>
        {run && <>
          <ConfirmDialog open={confirmCancel && canOperate && canCancelRun(run.status)} onClose={() => setConfirmCancel(false)} title={tx("Bu çalıştırma iptal edilsin mi?")} confirmLabel="İptali onayla"
            description={tx("Agent'a iptal isteği iletilir. Agent işlemi durdurana kadar çalıştırma iptal edilmiş sayılmaz; yarım kalan yedek kullanılamaz.")}
            pending={actions.cancel.isPending} error={actions.cancel.error ? errorMessage(actions.cancel.error) : undefined} onConfirm={() => void perform('cancel')} />
          {notice && run.status !== 'Succeeded' && !(run.status === 'Cancelled' && notice.startsWith(tx('İptal isteği'))) && <Notice tone="success">{notice}</Notice>}
          {actions.retry.error && <Notice tone="error">{errorMessage(actions.retry.error)}</Notice>}
          {actions.cancel.error && !confirmCancel && <Notice tone="error">{errorMessage(actions.cancel.error)}</Notice>}
          <Tabs label={tx("Backup Run bölümleri")} idPrefix="run" value={tab} onChange={setTab} items={tabs} />
          <div role="tabpanel" id={`run-panel-${tab}`} aria-labelledby={`run-tab-${tab}`} className="space-y-6">
            {tab === 'summary' && <>
              {run.sizeAnomalyStatus === 'SizeAnomalyWarning' && <Notice tone="warning">{tx("Yedek başarıyla tamamlandı. Boyut uyarısı yedeği geçersiz kılmaz; beklenmeyen veri değişikliklerini inceleyin.")}</Notice>}
              {run.sizeAnomalyStatus === 'BaselinePending' && <p className="text-sm text-text-muted">{tx("Karşılaştırma için en az 3 önceki başarılı yedek gerekir.")}</p>}
              <SizeAnomalyComparison run={run} />
              {(run.status === 'Failed' || run.status === 'AgentLost') && <Section tone="danger" title={tx("Çalıştırma başarısız oldu")}>
                <div className="space-y-1 text-sm">
                  <p className="font-medium text-error">{failureMessage(run.reasonCode ?? run.retry.lastFailureCode)}</p>
                  {run.reasonCode && <p className="text-text-muted">{tx("Hata kodu:")}{' '}{run.reasonCode}</p>}
                  <p className="text-text">{run.retry.lastFailureRetryable === false ? tx('Bu hata otomatik olarak yeniden denenmez. Elle yeniden deneme, kalan deneme hakkı ve işin çakışma politikasına bağlıdır.') : tx('Otomatik yeniden deneme sona erdi. Elle yeniden deneme, kalan deneme hakkı ve işin çakışma politikasına bağlıdır.')}</p>
                </div>
              </Section>}
              {run.status === 'RetryPending' && <Notice tone="warning">{tx("Otomatik yeniden deneme bekleniyor. Sonraki deneme:")}{' '}{formatTimestamp(run.retry.nextAttemptAt)}. {failureMessage(run.retry.lastFailureCode)}</Notice>}
              {run.status === 'CancellationRequested' && <Notice tone="warning">{tx("İptal istendi; çalıştırma henüz iptal edilmiş değildir. Ajan sonucu bildirdiğinde durum güncellenir.")}</Notice>}
              {isActiveRun(run.status) && latest && <Section title={tx("Güncel deneme")} description={tx("{0}. deneme · {1}", { "0": latest.number, "1": progressStageLabel(latest.progressStage) })}><AttemptProgress attempt={latest} /></Section>}
              <Section title={tx("Çalıştırma özeti")}>
                <Facts columns={3}>
                  <Fact label={tx("Veritabanı")}>{run.configuration.database}</Fact>
                  <Fact label={tx("Agent")}>{run.agentName ?? '—'}</Fact>
                  <Fact label={tx("Tetikleyici")}>{triggerLabel(run.triggerType)}</Fact>
                  <Fact label={tx("Yedek türü")}>{tx(run.backupType)}</Fact>
                  <Fact label={tx("Veritabanı motoru")}>{run.configuration.engine}</Fact>
                  <Fact label={tx("Deneme sayısı")}>{run.currentAttemptNumber} / {run.retry.maxAttempts}</Fact>
                  <Fact label={tx("Başlangıç")}>{formatTimestamp(run.startedAt)}</Fact>
                  <Fact label={tx("Bitiş")}>{formatTimestamp(run.completedAt)}</Fact>
                  <Fact label={tx("Süre")}>{formatDuration(run.startedAt, run.completedAt)}</Fact>
                </Facts>
              </Section>
            </>}
            {tab === 'attempts' && (run.attempts.length === 0
              ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={History} title={tx("Henüz deneme oluşturulmadı.")} /></div>
              : run.attempts.map((attempt) => <Section key={attempt.id} title={tx("{0}. deneme · {1}", { "0": attempt.number, "1": triggerLabel(attempt.trigger) })} actions={<><StatusPill status={attempt.status} /><ReplicationBadge replication={attempt.replication} /></>}>
                <div className="space-y-4">
                  <Facts columns={3}>
                    <Fact label={tx("Oluşturulma")}>{formatTimestamp(attempt.createdAt)}</Fact>
                    <Fact label={tx("Başlangıç")}>{formatTimestamp(attempt.startedAt)}</Fact>
                    <Fact label={tx("Bitiş")}>{formatTimestamp(attempt.completedAt)}</Fact>
                    <Fact label={tx("Süre")}>{formatDuration(attempt.startedAt, attempt.completedAt)}</Fact>
                    <Fact label={tx("Aşama")}>{progressStageLabel(attempt.progressStage)}</Fact>
                    <Fact label={tx("Son ilerleme")}>{formatTimestamp(attempt.progressAt)}</Fact>
                  </Facts>
                  <AttemptProgress attempt={attempt} />
                  {attempt.reasonCode && <div className="text-sm"><p className="text-error">{failureMessage(attempt.reasonCode)}</p><p className="mt-1 text-text-muted">{tx("Hata kodu:")}{' '}{attempt.reasonCode}</p>{attempt.retryable != null && <p className="mt-1 text-text-muted">{attempt.retryable ? tx('Otomatik yeniden denemeye uygun (politika sınırları içinde).') : tx('Otomatik yeniden denemeye uygun değil; elle yeniden deneme ayrı bir işlemdir.')}</p>}</div>}
                  <section aria-label={tx("{0}. deneme çıktıları", { "0": attempt.number })} className="space-y-3 border-t border-border pt-4">
                    <h3 className="text-sm font-medium text-text-heading">{tx("Yedek çıktıları")}</h3>
                    {attempt.artifacts.length === 0 ? <p className="text-sm text-text-muted">{tx("Bu denemede kayıtlı yedek çıktısı yok.")}</p> : attempt.artifacts.map((artifact) => <div key={artifact.id} className="space-y-2"><ArtifactDetails artifact={{ ...artifact, jobName: artifact.jobName ?? run.jobName, agentName: artifact.agentName ?? run.agentName, databaseName: artifact.databaseName ?? run.configuration.database }} /><Link className={`text-sm ${linkClass}`} to={`/tenants/${tenantId}/artifacts/${artifact.id}`}>{tx("Yedek çıktısını aç")}</Link></div>)}
                  </section>
                </div>
              </Section>))}
            {tab === 'replication' && <ReplicationSection replication={run.replication} />}
            {tab === 'timing' && <>
              <Section title={tx("Zaman çizelgesi")}>
                <Facts columns={3}>
                  <Fact label={tx("Oluşturulma")}>{formatTimestamp(run.createdAt)}</Fact>
                  <Fact label={tx("Planlanan zaman")}>{formatTimestamp(run.scheduledFor)}</Fact>
                  <Fact label={tx("Planlama gecikmesi")}>{run.scheduleDelaySeconds == null ? '—' : formatSeconds(run.scheduleDelaySeconds)}</Fact>
                  <Fact label={tx("Ajan alım son zamanı")}>{formatTimestamp(run.claimDeadline)}</Fact>
                  <Fact label={tx("Başlangıç")}>{formatTimestamp(run.startedAt)}</Fact>
                  <Fact label={tx("İptal isteği")}>{formatTimestamp(run.cancellationRequestedAt)}</Fact>
                  <Fact label={tx("Bitiş")}>{formatTimestamp(run.completedAt)}</Fact>
                  <Fact label={tx("Süre")}>{formatDuration(run.startedAt, run.completedAt)}</Fact>
                </Facts>
              </Section>
              <Section title={tx("Yeniden deneme ve saklama politikası")}>
                <Facts columns={3}>
                  <Fact label={tx("Yeniden deneme durumu")}>{retryStateLabels[run.retry.state] ?? run.retry.state}</Fact>
                  <Fact label={tx("Sonraki deneme")}>{formatTimestamp(run.retry.nextAttemptAt)}</Fact>
                  <Fact label={tx("Çakışma politikası")}>{run.retry.overlapPolicy === 'Skip' ? tx('Aktif iş varsa atla') : tx('Sıraya al')}</Fact>
                  <Fact label={tx("Yeniden deneme ilk gecikmesi")}>{formatSeconds(run.retry.policy.initialDelaySeconds)}</Fact>
                  <Fact label={tx("Yeniden deneme üst gecikmesi")}>{formatSeconds(run.retry.policy.maxDelaySeconds)}</Fact>
                  <Fact label={tx("Geri çekilme")}>{tx("{0} · Jitter {1}", { "0": run.retry.policy.backoff, "1": run.retry.policy.jitter })}</Fact>
                  <Fact label={tx("Saklama")}>{run.retention?.enabled ? formatSeconds(run.retention.keepForSeconds) : tx('Otomatik silme kapalı')}</Fact>
                  <Fact label={tx("Silme bekleme süresi")}>{run.retention?.enabled ? formatSeconds(run.retention.deleteGracePeriodSeconds) : '—'}</Fact>
                </Facts>
              </Section>
            </>}
          </div>
        </>}
      </QueryState>
    </div>
  )
}
