import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ProgressBar } from '@/components/data/progress-bar'
import { StatusPill } from '@/components/data/status-pill'
import { schedulePresetLabel } from '@/features/backup-jobs/job-wizard-model'
import { restoreIneligibility } from '@/features/artifacts/restore-eligibility'
import { useBackup } from '@/features/backups/hooks'
import { isActiveRun } from '@/features/backups/presentation'
import type { BackupRun } from '@/features/backups/types'
import { RestoreTestStarter } from '@/features/restores/verification'
import { useRestore } from '@/features/restores/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatBytes, formatDateTime } from '@/lib/format'
import { restorePath } from '@/lib/routes'
import type { FirstBackupItem, FirstBackupSettings } from './first-backup-model'
import { useDestinationLabel } from './use-destination-label'

type Phase = 'preparing' | 'backing-up' | 'verifying' | 'done'

const phaseLabel = (phase: Phase) => ({ preparing: tx('Hazırlanıyor'), 'backing-up': tx('Yedek alınıyor'), verifying: tx('Doğrulanıyor'), done: tx('Tamamlandı') })[phase]

/** The four stages the user sees, derived from the run status and the agent's reported stage. */
function runPhase(run: Pick<BackupRun, 'status'> & { attempts: BackupRun['attempts'] }): Phase {
  if (run.status === 'Succeeded') return 'done'
  const stage = run.attempts.at(-1)?.progressStage
  if (stage === 'Verifying') return 'verifying'
  return stage === 'BackingUp' || stage === 'Backup' || stage === 'Uploading' || stage === 'Finalizing' ? 'backing-up' : 'preparing'
}

/** "Sadece oluştur" landed here: the first backup waits for the scheduled time unless started now. */
export function ReadyStep({ settings, pending, failures, onStart, base }: { settings: FirstBackupSettings; pending: boolean; failures: unknown[]; onStart: () => void; base: string }) {
  useLanguage()
  const when = settings.scheduled ? tx('İlk yedek zamanlanan saatte alınacak ({0}).', { '0': schedulePresetLabel(settings.cronExpression) ?? tx('Özel zamanlama') }) : tx('Yedekleme elle çalıştırılır; ilk yedeği siz başlatırsınız.')
  return <section aria-labelledby="first-backup-ready" className="space-y-4">
    <h2 id="first-backup-ready" className="text-lg font-semibold text-text-heading">{tx('Yedekleme oluşturuldu')}</h2>
    <p className="text-text">{when}</p>
    {failures.map((error, index) => <ErrorNotice key={index} title={tx('İlk yedek başlatılamadı')} error={error} />)}
    <div className="flex flex-wrap items-center gap-3">
      <Button type="button" size="lg" disabled={pending} onClick={onStart}>{pending ? tx('Başlatılıyor…') : tx('Şimdi başlat')}</Button>
      <Link className="text-primary hover:underline" to={base}>{tx('Ana sayfaya git')}</Link>
    </div>
  </section>
}

/** Per-item stage and progress, the finished result and the optional trial restore; "Arka planda devam etsin" leaves the page while the backups keep running. */
export function ProgressStep({ items, names, runIds, settings, base }: { items: readonly FirstBackupItem[]; names: (item: FirstBackupItem) => string; runIds: Record<string, string>; settings: FirstBackupSettings; base: string }) {
  useLanguage()
  const where = useDestinationLabel(settings)
  const [finished, setFinished] = useState<ReadonlySet<string>>(new Set())
  const allDone = items.every(item => finished.has(item.key))
  return <section aria-labelledby="first-backup-progress" className="space-y-4">
    <h2 id="first-backup-progress" className="text-lg font-semibold text-text-heading">{tx('İlk yedek')}</h2>
    <ul className="space-y-4">{items.map(item => <li key={item.key}>
      <ItemProgress runId={runIds[item.key]} name={names(item)} where={where} onFinished={() => setFinished(previous => new Set(previous).add(item.key))} />
    </li>)}</ul>
    <Link className="inline-block text-primary hover:underline" to={base}>{allDone ? tx('Ana sayfaya git') : tx('Arka planda devam etsin')}</Link>
  </section>
}

function ItemProgress({ runId, name, where, onFinished }: { runId: string; name: string; where: string; onFinished: () => void }) {
  useLanguage()
  const { tenantId } = useTenant()
  const query = useBackup(runId)
  const run = query.data
  const finishedNow = Boolean(run && !isActiveRun(run.status))
  useEffect(() => { if (finishedNow) onFinished() }, [finishedNow]) // eslint-disable-line react-hooks/exhaustive-deps
  if (query.error) return <ErrorNotice title={name} error={query.error} onRetry={() => void query.refetch()} />
  if (!run) return <p role="status" className="text-sm text-text-muted">{tx('{0}: Hazırlanıyor', { '0': name })}</p>
  if (run.status !== 'Succeeded' && !isActiveRun(run.status)) return <Notice tone="warning" title={tx('{0}: ilk yedek alınamadı', { '0': name })}>
    {run.reason ?? tx('Ayrıntı için yedeğin sayfasına bakın.')} <Link className="text-primary hover:underline" to={`/tenants/${tenantId}/backup-runs/${run.id}`}>{tx('Ayrıntı')}</Link>
  </Notice>
  const phase = runPhase(run)
  const percent = run.attempts.at(-1)?.progressPercent
  const artifact = run.attempts.flatMap(attempt => attempt.artifacts)[0]
  const shown = phase === 'done' ? 100 : percent
  return <div className="space-y-3 rounded-lg border border-border bg-surface p-4">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="font-medium text-text-heading">{name}</h3>
      {phase === 'done' ? <StatusPill level="safe">{phaseLabel(phase)}</StatusPill> : <StatusPill level="running">{phaseLabel(phase)}</StatusPill>}
    </div>
    <ProgressBar label={tx('{0}: ilerleme', { '0': name })} value={shown} valueText={shown == null ? phaseLabel(phase) : tx('{0}, %{1}', { '0': phaseLabel(phase), '1': Math.round(shown) })} />
    {/* The polite live region announces stage changes; the progress bar announces its own value. */}
    <p role="status" className="sr-only">{tx('{0}: {1}', { '0': name, '1': phaseLabel(phase) })}</p>
    {phase === 'done' && <div className="space-y-4">
      <p className="font-medium text-text-heading">{tx('İlk yedeğiniz alındı ve doğrulandı')}</p>
      <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-3">
        {artifact && <div><dt className="text-text-muted">{tx('Boyut')}</dt><dd>{formatBytes(artifact.sizeBytes)}</dd></div>}
        <div><dt className="text-text-muted">{tx('Zaman')}</dt><dd>{formatDateTime(run.completedAt)}</dd></div>
        <div><dt className="text-text-muted">{tx('Depolama')}</dt><dd>{where}</dd></div>
      </dl>
      {artifact && <TrialRestore tenantId={tenantId} artifact={artifact} />}
    </div>}
  </div>
}

function TrialRestore({ tenantId, artifact }: { tenantId: string; artifact: NonNullable<BackupRun['attempts'][number]['artifacts'][number]> }) {
  useLanguage()
  const [choice, setChoice] = useState<{ skipped: true } | { restoreId: string } | null>(null)
  const blocked = restoreIneligibility(artifact)
  return <Section title={tx('Deneme geri yüklemesi')} description={tx('Yedeği yeni bir veritabanı olarak geri yükler; asıl verinize dokunmaz.')}>
    {choice && 'skipped' in choice ? <p className="text-sm text-text-muted">{tx('Atlandı.')}</p>
      : choice ? <RestoreOutcome tenantId={tenantId} restoreId={choice.restoreId} />
      : blocked ? <p className="text-sm text-text-muted">{blocked}</p>
      : <div className="flex flex-wrap items-end gap-3">
        <RestoreTestStarter tenantId={tenantId} artifact={artifact} label={tx('Geri yüklemeyi dene')} onStarted={restoreId => setChoice({ restoreId })} />
        <Button type="button" variant="outline" onClick={() => setChoice({ skipped: true })}>{tx('Atla')}</Button>
      </div>}
  </Section>
}

function RestoreOutcome({ tenantId, restoreId }: { tenantId: string; restoreId: string }) {
  useLanguage()
  const restore = useRestore(restoreId)
  return <div role="status" className="flex flex-wrap items-center gap-3 text-sm">
    {restore.data ? <StatusPill status={restore.data.verificationOutcome ?? restore.data.status} domain={restore.data.verificationOutcome ? 'verification' : 'general'} /> : <span>{tx('Başlatıldı')}</span>}
    <Link className="text-primary hover:underline" to={restorePath(tenantId, restoreId)}>{tx('Ayrıntı')}</Link>
  </div>
}
