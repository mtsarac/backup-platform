import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { History } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { ProgressBar } from '@/components/data/progress-bar'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { Tabs } from '@/components/ui/tabs'
import { ArtifactDetails } from '@/features/artifacts/artifact-details'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { useBackup, useBackupActions } from '@/features/backups/hooks'
import { canCancelRun, canRetryRun, isActiveRun, progressStageLabel, retryStateLabels, triggerLabel } from '@/features/backups/presentation'
import { ReplicationBadge, ReplicationSection } from '@/features/backups/replication'
import { SizeAnomalyBadge, SizeAnomalyComparison } from '@/features/backups/size-anomaly'
import type { BackupAttempt, BackupRun } from '@/features/backups/types'
import { linkClass } from '@/components/ui/form-styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { describeFailure } from '@/lib/api/failure-messages'
import { formatDateTime, formatDuration, formatSeconds } from '@/lib/format'

type Tab = 'summary' | 'attempts' | 'replication'

/** Everything the run page keeps out of sight: identifiers, trigger and planned time, attempt counts, timings and retry/retention policy. */
function runTechnicalItems(run: BackupRun): TechnicalItem[] {
  const { retry, retention } = run
  return [
    { label: tx("Yedekleme denemesi kimliği"), value: run.id },
    { label: tx("Tetikleyici"), value: triggerLabel(run.triggerType) },
    { label: tx("Planlanan zaman"), value: formatDateTime(run.scheduledFor) },
    { label: tx("Planlama gecikmesi"), value: run.scheduleDelaySeconds == null ? '—' : formatSeconds(run.scheduleDelaySeconds) },
    { label: tx("Yedek türü"), value: tx(run.backupType) },
    { label: tx("Veritabanı motoru"), value: run.configuration.engine },
    { label: tx("Deneme sayısı"), value: `${run.currentAttemptNumber} / ${retry.maxAttempts}` },
    { label: tx("Oluşturulma"), value: formatDateTime(run.createdAt) },
    { label: tx("Yedekleme bilgisayarı alım son zamanı"), value: formatDateTime(run.claimDeadline) },
    { label: tx("İptal isteği"), value: formatDateTime(run.cancellationRequestedAt) },
    { label: tx("Bitiş"), value: formatDateTime(run.completedAt) },
    { label: tx("Yeniden deneme durumu"), value: retryStateLabels[retry.state] ?? retry.state },
    { label: tx("Sonraki deneme"), value: formatDateTime(retry.nextAttemptAt) },
    { label: tx("Çakışma politikası"), value: retry.overlapPolicy === 'Skip' ? tx('Aktif iş varsa atla') : tx('Sıraya al') },
    { label: tx("Yeniden deneme ilk gecikmesi"), value: formatSeconds(retry.policy.initialDelaySeconds) },
    { label: tx("Yeniden deneme üst gecikmesi"), value: formatSeconds(retry.policy.maxDelaySeconds) },
    { label: tx("Geri çekilme"), value: tx("{0} · Jitter {1}", { "0": retry.policy.backoff, "1": retry.policy.jitter }) },
    { label: tx("Saklama"), value: retention?.enabled ? formatSeconds(retention.keepForSeconds) : tx('Otomatik silme kapalı') },
    { label: tx("Silme bekleme süresi"), value: retention?.enabled ? formatSeconds(retention.deleteGracePeriodSeconds) : '—' },
  ]
}

function attemptTechnicalItems(attempt: BackupAttempt): TechnicalItem[] {
  return [
    { label: tx("Oluşturulma"), value: formatDateTime(attempt.createdAt) },
    { label: tx("Bitiş"), value: formatDateTime(attempt.completedAt) },
    { label: tx("Son ilerleme"), value: formatDateTime(attempt.progressAt) },
    ...(attempt.retryable == null ? [] : [{ label: tx("Otomatik yeniden deneme"), value: attempt.retryable ? tx('Otomatik yeniden denemeye uygun (politika sınırları içinde).') : tx('Otomatik yeniden denemeye uygun değil; elle yeniden deneme ayrı bir işlemdir.') }]),
  ]
}

function AttemptProgress({ attempt }: { attempt: BackupAttempt }) {
  useLanguage()
  const percent = attempt.progressPercent == null ? undefined : Math.min(100, Math.max(0, attempt.progressPercent))
  return <div className="space-y-2">
    <p className="text-sm text-text-muted">{tx("İlerleme:")}{' '}{percent == null ? tx('Yüzde bildirilmedi') : tx('%{0}', { '0': percent.toLocaleString(currentLocale(), { maximumFractionDigits: 1 }) })}</p>
    <ProgressBar label={tx("{0}. deneme ilerlemesi", { "0": attempt.number })} value={percent} />
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
  const busy = actions.retry.isPending || actions.cancel.isPending || actions.pause.isPending || actions.resume.isPending

  async function perform(action: 'retry' | 'cancel' | 'pause' | 'resume') {
    setNotice('')
    actions.retry.reset()
    actions.cancel.reset()
    actions.pause.reset()
    actions.resume.reset()
    try {
      const response: BackupRun = await actions[action].mutateAsync()
      setNotice(action === 'pause' ? tx('Duraklatma isteği iletildi.') : action === 'resume' ? tx('Aktarım devam etmek üzere sıraya alındı.') : action === 'retry' ? tx('Yeni deneme sıraya alındı.') : response.status === 'Cancelled' ? tx('Yedekleme denemesi iptal edildi.') : tx('İptal isteği iletildi. Yedekleme bilgisayarının işlemi durdurması bekleniyor.'))
      setConfirmCancel(false)
    } catch {
      // The mutation's central error mapping is rendered in the page; never fabricate a state.
    }
  }

  const tabs: { value: Tab; label: string }[] = [
    { value: 'summary', label: tx('Özet') },
    { value: 'attempts', label: run ? tx('Denemeler ({0})', { '0': run.attempts.length }) : tx('Denemeler') },
    ...(run?.replication ? [{ value: 'replication' as const, label: tx('Replikasyon') }] : []),
  ]
  const latest = run?.attempts.at(-1)
  const failureCode = run && (run.reasonCode ?? run.retry.lastFailureCode ?? (run.status === 'AgentLost' ? 'AgentLost' : null))

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: tx('Yedekleme denemeleri'), to: `/tenants/${tenantId}/backup-runs` }, { label: run?.jobName ?? tx('Yedekleme denemesi') }]}
        title={run?.jobName ?? tx('Yedekleme denemesi')}
        description={run ? `${run.configuration.database} · ${run.agentName ?? tx('Yedekleme bilgisayarı')}` : undefined}
        meta={run && <><StatusPill status={run.status} /><ReplicationBadge replication={run.replication} /><SizeAnomalyBadge run={run} /><span className="text-sm text-text-muted"><RelativeTime value={run.startedAt ?? run.createdAt} /></span></>}
        actions={run && canOperate && <>
          {canRetryRun(run.status) && <Button disabled={busy} onClick={() => void perform('retry')}>{actions.retry.isPending ? tx('Sıraya alınıyor…') : tx('Yeniden dene')}</Button>}
          {(run.status === 'Running' || run.status === 'Leased') && <Button disabled={busy || run.pauseRequested} onClick={() => void perform('pause')}>{actions.pause.isPending || run.pauseRequested ? tx('İletiliyor…') : tx('Duraklat')}</Button>}
          {run.status === 'Paused' && <Button disabled={busy} onClick={() => void perform('resume')}>{actions.resume.isPending ? tx('Sıraya alınıyor…') : tx('Devam Et')}</Button>}
          {canCancelRun(run.status) && <Button variant="destructive" disabled={busy} onClick={() => { actions.cancel.reset(); setConfirmCancel(true) }}>{actions.cancel.isPending ? tx('İletiliyor…') : tx('İptal et')}</Button>}
        </>}
      />
      <QueryState isPending={query.isPending} error={query.error} empty={!query.isPending && !run}>
        {run && <>
          <ConfirmDialog open={confirmCancel && canOperate && canCancelRun(run.status)} onClose={() => setConfirmCancel(false)} title={tx("Bu yedekleme denemesi iptal edilsin mi?")} confirmLabel={tx("İptali onayla")}
            consequence={{
              what: tx("Yedekleme bilgisayarına iptal isteği iletilir. İşlem durana kadar deneme iptal edilmiş sayılmaz; yarım kalan yedek kullanılamaz."),
              who: tx("Yalnızca bu yedekleme denemesi etkilenir; önceki yedekleriniz korunur."),
              reversible: tx("Hayır. İptal edilen denemeyi sürdüremezsiniz; yeni bir deneme başlatmanız gerekir."),
            }}
            pending={actions.cancel.isPending} error={actions.cancel.error} onConfirm={() => void perform('cancel')} />
          {notice && run.status !== 'Succeeded' && !(run.status === 'Cancelled' && notice.startsWith(tx('İptal isteği'))) && <Notice tone="success">{notice}</Notice>}
          {actions.retry.error && <ErrorNotice error={actions.retry.error} />}
          {actions.cancel.error && !confirmCancel && <ErrorNotice error={actions.cancel.error} />}
          {actions.pause.error && <ErrorNotice error={actions.pause.error} />}
          {actions.resume.error && <ErrorNotice error={actions.resume.error} />}
          <Tabs label={tx("Yedekleme denemesi bölümleri")} idPrefix="run" value={tab} onChange={setTab} items={tabs} />
          <div role="tabpanel" id={`run-panel-${tab}`} aria-labelledby={`run-tab-${tab}`} className="space-y-6">
            {tab === 'summary' && <>
              {run.sizeAnomalyStatus === 'SizeAnomalyWarning' && <Notice tone="warning">{tx("Yedek başarıyla tamamlandı. Boyut uyarısı yedeği geçersiz kılmaz; beklenmeyen veri değişikliklerini inceleyin.")}</Notice>}
              {run.sizeAnomalyStatus === 'BaselinePending' && <p className="text-sm text-text-muted">{tx("Karşılaştırma için en az 3 önceki başarılı yedek gerekir.")}</p>}
              <SizeAnomalyComparison run={run} />
              {(run.status === 'Failed' || run.status === 'AgentLost') && <ErrorNotice title={tx("Yedekleme denemesi başarısız oldu")} {...describeFailure(failureCode)}
                action={canOperate ? tx("Sorunu giderdikten sonra “Yeniden dene” ile yeni bir deneme başlatın.") : tx("Yöneticinize danışın.")} />}
              {run.status === 'RetryPending' && <Notice tone="warning">{tx("Otomatik yeniden deneme bekleniyor. Sonraki deneme:")}{' '}{formatDateTime(run.retry.nextAttemptAt)}. {describeFailure(run.retry.lastFailureCode).what}</Notice>}
              {run.status === 'CancellationRequested' && <Notice tone="warning">{tx("İptal istendi; yedekleme denemesi henüz iptal edilmiş değildir. Yedekleme bilgisayarı sonucu bildirdiğinde durum güncellenir.")}</Notice>}
              {run.status === 'Paused' && <Notice tone="warning">{run.pauseReason === 'PausedByNetworkError' ? tx('Ağ kesintisi nedeniyle aktarım duraklatıldı. Bağlantıyı kontrol edip Devam Et ile sürdürün.') : tx('Aktarım duraklatıldı. Hazırlanmış yedek korunur; Devam Et ile aynı baytlardan sürdürülür.')}</Notice>}
              {isActiveRun(run.status) && latest && <Section title={tx("Güncel deneme")} description={tx("{0}. deneme · {1}", { "0": latest.number, "1": progressStageLabel(latest.progressStage) })}><AttemptProgress attempt={latest} /></Section>}
              <Section title={tx("Yedekleme denemesi özeti")}>
                <Facts columns={2}>
                  <Fact label={tx("Veritabanı")}>{run.configuration.database}</Fact>
                  <Fact label={tx("Yedekleme bilgisayarı")}>{run.agentName ?? '—'}</Fact>
                  <Fact label={tx("Başlangıç")}><RelativeTime value={run.startedAt} /></Fact>
                  <Fact label={tx("Süre")}>{formatDuration(run.startedAt, run.completedAt)}</Fact>
                </Facts>
              </Section>
              <div role="region" aria-label={tx("Yedekleme denemesi ayrıntıları")}><TechnicalDetails items={runTechnicalItems(run)} /></div>
            </>}
            {tab === 'attempts' && (run.attempts.length === 0
              ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={History} message={tx("Henüz deneme oluşturulmadı.")} /></div>
              : run.attempts.map((attempt) => <Section key={attempt.id} title={tx("{0}. deneme · {1}", { "0": attempt.number, "1": triggerLabel(attempt.trigger) })} actions={<><StatusPill status={attempt.status} /><ReplicationBadge replication={attempt.replication} /></>}>
                <div className="space-y-4">
                  <Facts columns={3}>
                    <Fact label={tx("Başlangıç")}><RelativeTime value={attempt.startedAt} /></Fact>
                    <Fact label={tx("Süre")}>{formatDuration(attempt.startedAt, attempt.completedAt)}</Fact>
                    <Fact label={tx("Aşama")}>{progressStageLabel(attempt.progressStage)}</Fact>
                  </Facts>
                  <AttemptProgress attempt={attempt} />
                  {attempt.reasonCode && <ErrorNotice failureCode={attempt.reasonCode} />}
                  <TechnicalDetails items={attemptTechnicalItems(attempt)} />
                  <section aria-label={tx("{0}. deneme çıktıları", { "0": attempt.number })} className="space-y-3 border-t border-border pt-4">
                    <h3 className="text-sm font-medium text-text-heading">{tx("Yedek dosyaları")}</h3>
                    {attempt.artifacts.length === 0 ? <p className="text-sm text-text-muted">{tx("Bu denemede kayıtlı yedek dosyası yok.")}</p> : attempt.artifacts.map((artifact) => <div key={artifact.id} className="space-y-2"><ArtifactDetails artifact={{ ...artifact, jobName: artifact.jobName ?? run.jobName, agentName: artifact.agentName ?? run.agentName, databaseName: artifact.databaseName ?? run.configuration.database }} /><Link className={`text-sm ${linkClass}`} to={`/tenants/${tenantId}/artifacts/${artifact.id}`}>{tx("Yedek dosyasını aç")}</Link></div>)}
                  </section>
                </div>
              </Section>))}
            {tab === 'replication' && <ReplicationSection replication={run.replication} />}
          </div>
        </>}
      </QueryState>
    </div>
  )
}
