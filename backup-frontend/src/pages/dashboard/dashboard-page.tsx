import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Check, Circle, Plus } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { ProgressBar } from '@/components/data/progress-bar'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { useRunBackupJob } from '@/features/backup-jobs/hooks'
import { jobStatusLabel, jobStatusLevel } from '@/features/backup-jobs/job-status'
import { type Badge, type Band, countRows, formatDay, headerBadge, lastSucceededDay, pickBand, type ProtectionRow } from '@/features/dashboard/protection'
import type { BackupRun } from '@/features/backups/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { failureMessage } from '@/lib/api/failure-messages'
import { formatBytes, formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import { ProtectionStrip } from './protection-strip'
import { useProtection } from './use-protection'

type Protection = ReturnType<typeof useProtection>

const badgeView: Record<Badge, () => { level: 'urgent' | 'neutral' | 'safe'; text: string }> = {
  attention: () => ({ level: 'urgent', text: tx('Dikkat gerekiyor') }),
  none: () => ({ level: 'neutral', text: tx('Henüz yedekleme yok') }),
  first: () => ({ level: 'neutral', text: tx('İlk yedek bekleniyor') }),
  current: () => ({ level: 'safe', text: tx('Hepsi güncel') }),
}

/** One plain sentence about a run: why it failed, or what it is waiting for. */
function runNote(run: BackupRun): string {
  if (run.reasonCode) return failureMessage(run.reasonCode)
  if (run.status === 'RetryPending') return tx('Yeniden deneme bekleniyor.')
  if (run.status === 'CancellationRequested') return tx('Yedekleme bilgisayarı iptali henüz tamamlamadı.')
  return '—'
}

function AttentionBand({ band, base }: { band: Band; base: string }) {
  const run = useRunBackupJob()
  const [started, setStarted] = useState<string>()
  const [error, setError] = useState<unknown>(null)
  const { row } = band
  const action = band.kind === 'attention' ? band.action : null
  return <section aria-label={band.kind === 'attention' ? tx('Dikkat gerekiyor') : tx('Çalışan yedekleme')} className={`space-y-2 rounded-lg border p-4 ${band.kind === 'attention' ? 'border-error-border bg-error-subtle' : 'border-border bg-surface'}`}>
    <div className="flex flex-wrap items-center justify-between gap-3">
      <p className="min-w-0 font-medium text-text-heading">{band.text}</p>
      <div className="flex flex-wrap items-center gap-2">
        {action === 'runNow' && <Button disabled={run.isPending} onClick={() => { setError(null); void run.run(row.job.id).then(result => { if (result) setStarted(result.id) }).catch(setError) }}>{tx('Şimdi yedekle')}</Button>}
        {action === 'checkComputer' && <Link className={buttonVariants()} to={`${base}/agents/${row.job.agentId}`}>{tx('Bilgisayarı kontrol et')}</Link>}
        {action === 'manageStorage' && <Link className={buttonVariants()} to={`${base}/storage`}>{tx('Depolamayı yönet')}</Link>}
        {action === 'retryRestore' && row.lastSuccessId && <Link className={buttonVariants()} to={`${base}/backup-runs/${row.lastSuccessId}/restore`}>{tx('Yeniden dene')}</Link>}
        <Link className={buttonVariants({ variant: 'outline' })} to={`${base}/backup-jobs/${row.job.id}`}>{tx('Ayrıntıyı gör')}</Link>
      </div>
    </div>
    {started && <p className="text-sm">{tx('Yedekleme denemesi başlatıldı')} · <Link className={linkClass} to={`${base}/backup-runs/${started}`}>{tx('Denemeyi aç')}</Link></p>}
    {error !== null && <ErrorNotice title={tx('İşlem tamamlanamadı')} error={error} />}
  </section>
}

function Row({ row, base }: { row: ProtectionRow; base: string }) {
  const { job } = row
  const day = lastSucceededDay(row.days)
  return <li className="grid items-center gap-x-4 gap-y-3 px-4 py-3 md:grid-cols-[minmax(0,1fr)_9rem_auto]">
    <div className="min-w-0 space-y-2">
      <div>
        <Link className={`break-words font-medium ${linkClass}`} to={`${base}/backup-jobs/${job.id}`}>{job.name}</Link>
        <p className="truncate text-sm text-text-muted">{job.configuration.database}</p>
      </div>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <ProtectionStrip days={row.days} jobName={job.name} />
        <p className="text-sm text-text-muted">{tx('Son başarılı')}: {day ? formatDay(day) : row.hasSucceeded ? tx('14 günden önce') : '—'}</p>
      </div>
    </div>
    <div className="min-w-0 space-y-0.5">
      <StatusPill level={jobStatusLevel[row.status]}>{jobStatusLabel(row.status, row.percent)}</StatusPill>
      {row.note && <p className="text-sm text-text-muted">{row.note}</p>}
    </div>
    <div className="flex flex-wrap items-center gap-2">
      {row.lastSuccessId
        ? <Link className={buttonVariants({ variant: 'outline' })} to={`${base}/backup-runs/${row.lastSuccessId}/restore`} aria-label={tx('{0} için geri yükle', { '0': job.name })}>{tx('Geri yükle')}</Link>
        : <><Button variant="outline" disabled aria-label={tx('{0} için geri yükle', { '0': job.name })}>{tx('Geri yükle')}</Button><span className="text-sm text-text-muted">{tx('henüz yedek yok')}</span></>}
    </div>
  </li>
}

/** Jobs that can never run again: shown quietly, apart from the protected data, with their computer's name so same-named jobs can be told apart. */
function RevokedJobs({ rows, base }: { rows: ProtectionRow[]; base: string }) {
  return <section aria-label={tx('Bilgisayarı iptal edilmiş işler')} className="overflow-hidden rounded-lg border border-dashed border-border">
    <header className="border-b border-border px-4 py-2"><h3 className="font-medium text-text-muted">{tx('Bilgisayarı iptal edilmiş işler')}</h3></header>
    <ul className="divide-y divide-border">{rows.map(({ job }) => <li key={job.id} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-4 py-3">
      <div className="min-w-0">
        <Link className={`break-words font-medium ${linkClass}`} to={`${base}/backup-jobs/${job.id}`}>{job.name}</Link>
        <p className="truncate text-sm text-text-muted">{job.agentName ?? tx('Adsız bilgisayar')} · {job.configuration.database}</p>
      </div>
      <StatusPill level={jobStatusLevel.revoked}>{jobStatusLabel('revoked')}</StatusPill>
    </li>)}</ul>
  </section>
}

function ProtectedData({ rows, revokedRows, agents, base }: { rows: ProtectionRow[]; revokedRows: ProtectionRow[]; agents: Protection['agents']; base: string }) {
  const groups = new Map<string, ProtectionRow[]>()
  for (const row of rows) groups.set(row.job.agentId, [...groups.get(row.job.agentId) ?? [], row])
  return <Section title={tx('Korunan veriler')}>
    <div className="space-y-4">
      {rows.length === 0
        ? <p className="rounded-lg border border-dashed border-border px-4 py-6 text-center text-sm text-text-muted">{tx('Korunan verileriniz burada görünecek.')}</p>
        : [...groups].map(([agentId, items]) => {
          const connectivity = agents.find(agent => agent.agentId === agentId)?.connectivity ?? 'unknown'
          const sorted = items.toSorted((a, b) => Number(b.baseStatus === 'attention') - Number(a.baseStatus === 'attention') || a.score - b.score || a.job.name.localeCompare(b.job.name))
          return <section key={agentId} aria-label={items[0].job.agentName ?? agentId} className="overflow-hidden rounded-lg border border-border bg-surface">
            <header className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2">
              <h3 className="font-medium text-text-heading">{items[0].job.agentName ?? agentId}</h3>
              <StatusPill status={connectivity} />
            </header>
            <ul className="divide-y divide-border">{sorted.map(row => <Row key={row.job.id} row={row} base={base} />)}</ul>
          </section>
        })}
      {revokedRows.length > 0 && <RevokedJobs rows={revokedRows} base={base} />}
    </div>
  </Section>
}

function Steps({ p, base }: { p: Protection; base: string }) {
  const steps = [
    { done: p.agents.length > 0, label: tx('Yedekleme bilgisayarı ekleyin'), to: `${base}/first-backup` },
    { done: p.rows.length > 0, label: tx('Yedeklenecek veriyi seçin'), to: `${base}/first-backup` },
    { done: p.hasSucceededEver, label: tx('İlk yedeğinizi alın'), to: `${base}/backup-jobs` },
    { done: p.hasRestoreTrial, label: tx('Yedeği geri yükleyerek deneyin'), to: `${base}/backup-runs?status=Succeeded` },
  ]
  const current = steps.findIndex(step => !step.done)
  return <Section title={tx('Başlangıç adımları')}>
    <ol className="divide-y divide-border rounded-lg border border-border bg-surface">{steps.map((step, index) => <li key={step.label} aria-current={index === current ? 'step' : undefined} className="flex flex-wrap items-center justify-between gap-3 px-4 py-3">
      <span className="flex items-center gap-2">{step.done ? <Check aria-hidden="true" className="size-4 text-success" /> : <Circle aria-hidden="true" className="size-4 text-text-muted" />}
        <span className={step.done ? 'text-text-muted' : 'font-medium text-text-heading'}>{step.label}</span>{step.done && <span className="sr-only">{tx('Tamamlandı')}</span>}</span>
      {index === current && <Link className={buttonVariants()} to={step.to}>{tx('Başla')}</Link>}
    </li>)}</ol>
  </Section>
}

function StorageCard({ p, base }: { p: Protection; base: string }) {
  const quota = p.quota
  const limited = quota && !quota.unlimited && quota.limitBytes != null && quota.limitBytes > 0
  const percent = limited ? (quota.usedBytes / quota.limitBytes!) * 100 : null
  return <Section title={tx('Depolama')} actions={<Link className={linkClass} to={`${base}/storage`}>{tx('Yönet')}</Link>}>
    <p className="text-lg font-semibold tabular-nums text-text-heading">{formatBytes(p.dashboard?.artifacts.sizeBytes ?? 0)}</p>
    {limited && <div className="mt-2 space-y-1">
      <ProgressBar label={tx('Kota kullanımı')} value={percent} valueText={`${Math.round(percent!)}%`} tone={quota.state === 'Normal' ? 'primary' : 'warning'} />
      <p className="text-sm text-text-muted">{tx('Kota: {0}', { '0': formatBytes(quota.limitBytes!) })}</p>
    </div>}
  </Section>
}

function NextCard({ p, base }: { p: Protection; base: string }) {
  const revoked = new Set(p.revokedRows.map(row => row.job.id))
  const upcoming = (p.dashboard?.upcomingJobs ?? []).filter(job => job.schedule?.nextOccurrenceAt && !revoked.has(job.id))
    .toSorted((a, b) => a.schedule!.nextOccurrenceAt!.localeCompare(b.schedule!.nextOccurrenceAt!))[0]
  const last = p.lastSuccess
  return <Section title={tx('Sıradaki ve son yedek')}><dl className="space-y-2 text-sm">
    <div><dt className="text-text-muted">{tx('Sıradaki')}</dt><dd>{upcoming ? <><Link className={linkClass} to={`${base}/backup-jobs/${upcoming.id}`}>{upcoming.name}</Link> · {formatDateTime(upcoming.schedule!.nextOccurrenceAt)}</> : tx('Etkin zamanlama yok.')}</dd></div>
    <div><dt className="text-text-muted">{tx('Son başarılı yedek')}</dt><dd>{last ? <><Link className={linkClass} to={`${base}/backup-runs/${last.id}`}>{last.jobName}</Link> · <RelativeTime value={last.completedAt ?? last.createdAt} /></> : '—'}</dd></div>
  </dl></Section>
}

function ActivityCard({ runs, base }: { runs: BackupRun[]; base: string }) {
  return <Section title={tx('Son etkinlik')} actions={<Link className={linkClass} to={`${base}/backup-runs`}>{tx('Tüm geçmiş')}</Link>}>
    {runs.length === 0 ? <p className="text-sm text-text-muted">{tx('Henüz yedekleme kaydı yok.')}</p>
      : <ul className="space-y-2">{runs.slice(0, 4).map(run => <li key={run.id} className="space-y-0.5 text-sm">
        <div className="flex flex-wrap items-center justify-between gap-2"><Link className={`font-medium ${linkClass}`} to={`${base}/backup-runs/${run.id}`}>{run.jobName}</Link><StatusPill status={run.status} /></div>
        <p className="text-text-muted"><RelativeTime value={run.startedAt ?? run.createdAt} />{run.reasonCode || run.status === 'RetryPending' || run.status === 'CancellationRequested' ? <> · {runNote(run)}</> : null}</p>
      </li>)}</ul>}
  </Section>
}

export function DashboardPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const base = `/tenants/${tenantId}`
  const p = useProtection(tenantId)
  const counts = countRows(p.rows)
  const badge = badgeView[headerBadge(p.rows)]()
  const band = pickBand(p.rows)
  const empty = p.rows.length === 0
  return <>
    <PageHeader title={tx('Ana sayfa')}
      meta={!p.isPending && !p.error && <StatusPill level={badge.level}>{badge.text}</StatusPill>}
      description={!p.isPending && !empty && [counts.total > 0 || counts.first === 0 ? tx('{0} veri korunuyor · {1} güncel · {2} dikkat gerekiyor', { '0': counts.total, '1': counts.current, '2': counts.attention }) : null, counts.first > 0 ? tx('{0} ilk yedek bekliyor', { '0': counts.first }) : null].filter(Boolean).join(' · ')}
      actions={<>
        <Link className={buttonVariants({ variant: 'outline' })} to={`${base}/backup-runs?status=Succeeded`}>{tx('Geri yükle')}</Link>
        <Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" className="size-4" />{tx('Yedekleme işi oluştur')}</Link>
      </>} />
    <QueryState isPending={p.isPending} error={p.error} onRetry={p.refetch}>
      <div className="space-y-6">
        {band && <AttentionBand band={band} base={base} />}
        {empty && <Steps p={p} base={base} />}
        <ProtectedData rows={p.rows} revokedRows={p.revokedRows} agents={p.agents} base={base} />
        <div className="grid gap-6 md:grid-cols-3">
          {!empty && <NextCard p={p} base={base} />}
          <StorageCard p={p} base={base} />
          {!empty && <ActivityCard runs={p.dashboard?.recentRuns ?? []} base={base} />}
        </div>
      </div>
    </QueryState>
  </>
}
