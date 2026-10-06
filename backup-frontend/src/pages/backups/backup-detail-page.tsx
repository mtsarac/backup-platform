import { useState } from 'react'
import { ArrowLeft } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ArtifactDetails } from '@/features/artifacts/artifact-details'
import { useBackup, useBackupActions } from '@/features/backups/hooks'
import { canCancelRun, canRetryRun, formatDuration, formatSeconds, formatTimestamp, progressStageLabel, retryStateLabels, triggerLabel } from '@/features/backups/presentation'
import type { BackupRun } from '@/features/backups/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'

export function BackupDetailPage() {
  const { id = '' } = useParams()
  const { tenantId, canOperate } = useTenant()
  const query = useBackup(id)
  const actions = useBackupActions(id)
  const [notice, setNotice] = useState('')
  const run = query.data
  const busy = actions.retry.isPending || actions.cancel.isPending
  const mutationError = actions.retry.error ?? actions.cancel.error

  async function perform(action: 'retry' | 'cancel') {
    setNotice('')
    actions.retry.reset()
    actions.cancel.reset()
    try {
      const response: BackupRun = await actions[action].mutateAsync()
      setNotice(action === 'retry' ? 'Yeni deneme sıraya alındı.' : response.status === 'Cancelled' ? 'Çalıştırma iptal edildi.' : 'İptal isteği iletildi. Ajanın işlemi durdurması bekleniyor.')
    } catch {
      // The mutation's central error mapping is rendered below; never fabricate a state.
    }
  }

  return (
    <div className="space-y-5">
      <Link to={`/tenants/${tenantId}/backup-runs`} className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"><ArrowLeft className="size-4" />Yedekleme geçmişine dön</Link>
      <QueryState isPending={query.isPending} error={query.error} empty={!query.isPending && !run}>
        {run && <>
          <header className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <h1 className="text-2xl font-semibold text-text-heading">{run.jobName ?? 'Yedekleme çalıştırması'}</h1>
              <p className="mt-1 text-sm text-text-muted">{run.configuration.database} · {run.agentName ?? 'Ajan'} · {triggerLabel(run.triggerType)}</p>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <StatusPill status={run.status} />
              {canOperate && canRetryRun(run.status) && <Button disabled={busy} onClick={() => void perform('retry')}>{actions.retry.isPending ? 'Sıraya alınıyor…' : 'Yeniden dene'}</Button>}
              {canOperate && canCancelRun(run.status) && <Button variant="destructive" disabled={busy} onClick={() => void perform('cancel')}>{actions.cancel.isPending ? 'İletiliyor…' : 'İptal et'}</Button>}
            </div>
          </header>
          {notice && run.status !== 'Succeeded' && <p role="status" className="rounded-lg border border-border bg-surface p-3 text-sm">{notice}</p>}
          {mutationError && <p role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-3 text-sm text-error">{errorMessage(mutationError)}</p>}
          {(run.status === 'Failed' || run.status === 'AgentLost') && <section aria-label="Başarısız çalıştırma" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-sm">
            <p className="font-medium text-error">{failureMessage(run.reasonCode ?? run.retry.lastFailureCode)}</p>
            {run.reasonCode && <p className="mt-1 text-text-muted">Hata kodu: {run.reasonCode}</p>}
            <p className="mt-2 text-text">{run.retry.lastFailureRetryable === false ? 'Bu hata otomatik olarak yeniden denenmez. Elle yeniden deneme, kalan deneme hakkı ve işin çakışma politikasına bağlıdır.' : 'Otomatik yeniden deneme sona erdi. Elle yeniden deneme, kalan deneme hakkı ve işin çakışma politikasına bağlıdır.'}</p>
          </section>}
          {run.status === 'RetryPending' && <p role="status" className="rounded-lg border border-border bg-surface p-4 text-sm">Otomatik yeniden deneme bekleniyor. Sonraki deneme: {formatTimestamp(run.retry.nextAttemptAt)}. {failureMessage(run.retry.lastFailureCode)}</p>}
          {run.status === 'CancellationRequested' && <p role="status" className="rounded-lg border border-border bg-surface p-4 text-sm">İptal istendi; çalıştırma henüz iptal edilmiş değildir. Ajan sonucu bildirdiğinde durum güncellenir.</p>}
          <section aria-labelledby="run-summary" className="rounded-lg border border-border bg-surface p-5">
            <h2 id="run-summary" className="mb-4 text-lg font-semibold text-text-heading">Çalıştırma özeti</h2>
            <dl className="grid gap-4 text-sm sm:grid-cols-2 xl:grid-cols-3">
              {[
                ['Yedek türü', run.backupType], ['Veritabanı motoru', run.configuration.engine],
                ['Oluşturulma', formatTimestamp(run.createdAt)], ['Başlangıç', formatTimestamp(run.startedAt)],
                ['Bitiş', formatTimestamp(run.completedAt)], ['Süre', formatDuration(run.startedAt, run.completedAt)],
                ['Planlanan zaman', formatTimestamp(run.scheduledFor)], ['Planlama gecikmesi', run.scheduleDelaySeconds == null ? '—' : formatSeconds(run.scheduleDelaySeconds)],
                ['İptal isteği', formatTimestamp(run.cancellationRequestedAt)], ['Ajan alım son zamanı', formatTimestamp(run.claimDeadline)],
                ['Deneme sayısı', `${run.currentAttemptNumber} / ${run.retry.maxAttempts}`], ['Sonraki deneme', formatTimestamp(run.retry.nextAttemptAt)],
                ['Çakışma politikası', run.retry.overlapPolicy === 'Skip' ? 'Aktif iş varsa atla' : 'Sıraya al'],
                ['Saklama', run.retention?.enabled ? formatSeconds(run.retention.keepForSeconds) : 'Otomatik silme kapalı'],
                ['Silme bekleme süresi', run.retention?.enabled ? formatSeconds(run.retention.deleteGracePeriodSeconds) : '—'],
                ['Yeniden deneme ilk gecikmesi', formatSeconds(run.retry.policy.initialDelaySeconds)],
                ['Yeniden deneme üst gecikmesi', formatSeconds(run.retry.policy.maxDelaySeconds)],
                ['Geri çekilme', `${run.retry.policy.backoff} · Jitter ${run.retry.policy.jitter}`],
                ['Yeniden deneme durumu', retryStateLabels[run.retry.state] ?? run.retry.state],
              ].map(([label, value]) => <div key={label}><dt className="text-text-muted">{label}</dt><dd className="mt-1 break-words font-medium text-text">{value}</dd></div>)}
            </dl>
          </section>
          <section aria-labelledby="run-attempts" className="space-y-3">
            <h2 id="run-attempts" className="text-lg font-semibold text-text-heading">Denemeler</h2>
            {run.attempts.length === 0 && <p className="text-sm text-text-muted">Henüz deneme oluşturulmadı.</p>}
            {run.attempts.map((attempt) => {
              const percent = attempt.progressPercent == null ? undefined : Math.min(100, Math.max(0, attempt.progressPercent))
              return <article key={attempt.id} aria-label={`${attempt.number}. deneme`} className="space-y-4 rounded-lg border border-border bg-surface p-5">
                <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="font-medium text-text-heading">{attempt.number}. deneme · {triggerLabel(attempt.trigger)}</h3><StatusPill status={attempt.status} /></div>
                <dl className="grid gap-3 text-sm sm:grid-cols-2 xl:grid-cols-4">
                  {[
                    ['Oluşturulma', formatTimestamp(attempt.createdAt)], ['Başlangıç', formatTimestamp(attempt.startedAt)],
                    ['Bitiş', formatTimestamp(attempt.completedAt)], ['Süre', formatDuration(attempt.startedAt, attempt.completedAt)],
                    ['Aşama', progressStageLabel(attempt.progressStage)], ['Son ilerleme', formatTimestamp(attempt.progressAt)],
                  ].map(([label, value]) => <div key={label}><dt className="text-text-muted">{label}</dt><dd className="mt-1 text-text">{value}</dd></div>)}
                </dl>
                <div className="space-y-2">
                  <p className="text-sm text-text-muted">İlerleme: {percent == null ? 'Yüzde bildirilmedi' : `%${percent.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`}</p>
                  <div role="progressbar" aria-label={`${attempt.number}. deneme ilerlemesi`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="h-2 overflow-hidden rounded-full bg-surface-2">
                    {percent != null && <div className="h-full rounded-full bg-primary" style={{ width: `${percent}%` }} />}
                  </div>
                </div>
                {attempt.reasonCode && <div className="text-sm"><p className="text-error">{failureMessage(attempt.reasonCode)}</p><p className="mt-1 text-text-muted">Hata kodu: {attempt.reasonCode}</p>{attempt.retryable != null && <p className="mt-1 text-text-muted">{attempt.retryable ? 'Otomatik yeniden denemeye uygun (politika sınırları içinde).' : 'Otomatik yeniden denemeye uygun değil; elle yeniden deneme ayrı bir işlemdir.'}</p>}</div>}
                <section aria-label={`${attempt.number}. deneme çıktıları`} className="space-y-3">
                  <h4 className="text-sm font-medium text-text-heading">Yedek çıktıları</h4>
                  {attempt.artifacts.length === 0 ? <p className="text-sm text-text-muted">Bu denemede kayıtlı yedek çıktısı yok.</p> : attempt.artifacts.map((artifact) => <div key={artifact.id} className="space-y-2"><ArtifactDetails artifact={{ ...artifact, jobName: artifact.jobName ?? run.jobName, agentName: artifact.agentName ?? run.agentName, databaseName: artifact.databaseName ?? run.configuration.database }} /><Link className="text-sm text-primary hover:underline" to={`/tenants/${tenantId}/artifacts/${artifact.id}`}>Yedek çıktısını aç</Link></div>)}
                </section>
              </article>
            })}
          </section>
        </>}
      </QueryState>
    </div>
  )
}
