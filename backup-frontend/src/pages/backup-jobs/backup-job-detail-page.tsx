import { useEffect, useRef, useState } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Menu } from '@base-ui/react/menu'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useAgents } from '@/features/agents/hooks'
import { AgentLogs } from '@/features/agent-logs/agent-logs'
import { archiveBackupJob, getBackupJobHealth, getHealthTimeline } from '@/features/backup-jobs/api'
import { useBackupJobs, useRunBackupJob, useToggleBackupJob } from '@/features/backup-jobs/hooks'
import { jobStatus, jobStatusLabel, jobStatusLevel } from '@/features/backup-jobs/job-status'
import { JobSections } from '@/features/backup-jobs/job-sections'
import type { BackupJob } from '@/features/backup-jobs/types'
import { useBackup, useBackups } from '@/features/backups/hooks'
import { isActiveRun } from '@/features/backups/presentation'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { tx, useLanguage } from '@/lib/i18n'

/** One job: status, what to do about it, the actions and its settings. */
function JobDetail({ job }: { job: BackupJob }) {
  const { tenantId } = useTenant()
  const listSearch = (useLocation().state as { listSearch?: string } | null)?.listSearch ?? ''
  const navigate = useNavigate()
  const client = useQueryClient()
  const base = `/tenants/${tenantId}`
  const [confirm, setConfirm] = useState(false)
  const [startedRun, setStartedRun] = useState<string>()
  const [actionError, setActionError] = useState<unknown>(null)
  const health = useQuery({ queryKey: ['tenant', tenantId, 'backup-jobs', job.id, 'health'], queryFn: ({ signal }) => getBackupJobHealth(tenantId, job.id, signal) })
  const timeline = useQuery({ queryKey: ['tenant', tenantId, 'backup-jobs', 'health-timeline'], queryFn: ({ signal }) => getHealthTimeline(tenantId, signal) })
  const agents = useAgents(tenantId)
  const successes = useBackups({ status: 'Succeeded', jobId: job.id, page: 1, pageSize: 1 })
  const active = Boolean(job.lastRun && isActiveRun(job.lastRun.status))
  const lastRun = useBackup(active ? job.lastRun!.id : '')
  const run = useRunBackupJob()
  const toggle = useToggleBackupJob()
  const archive = useMutation({ mutationFn: () => archiveBackupJob(tenantId, job.id), onSuccess: async () => {
    await Promise.all(['backup-jobs', 'dashboard', 'backup-runs'].map(key => client.invalidateQueries({ queryKey: ['tenant', tenantId, key] })))
    navigate(`${base}/backup-jobs`)
  } })
  const days = timeline.data?.jobs.find(item => item.jobId === job.id)?.days ?? []
  const offline = agents.data?.find(agent => agent.agentId === job.agentId)?.connectivity === 'offline'
  const failedRecently = days.some(day => day.status === 'Failed')
  const hasSucceeded = (successes.data?.totalCount ?? 0) > 0
  const status = job.agentRevoked ? 'revoked' as const : health.data && timeline.data && successes.data && agents.data
    ? jobStatus({ job, health: health.data, failedRecently, hasSucceeded, offline, running: active }) : undefined
  const reasons = health.data ? Object.values(health.data.factors).map(factor => factor.reason) : []
  const reason = status === 'revoked' ? tx('Bu bilgisayar iptal edildi; yeni bir yedekleme işi oluşturun.')
    : status !== 'attention' ? null
    : offline ? tx('Yedekleme bilgisayarı çevrimdışı.')
    : !hasSucceeded && failedRecently ? tx('İlk yedek alınamadı.')
    : reasons.includes('backup_stale') || reasons.includes('backup_missing') ? tx('Son başarılı yedek, planlanan periyodu aştı.')
    : reasons.includes('capacity_critical') ? tx('Depolama alanı doluyor')
    : reasons.includes('no_verified_replica') || reasons.includes('replicas_missing') ? tx('Kullanılabilir doğrulanmış kopya yok.')
    : reasons.includes('restore_failed') ? tx('Son izole geri yükleme testi başarısız oldu.') : null
  // A run that just ended changes the status, the last success and the health: read them again once.
  const wasActive = useRef(active)
  useEffect(() => {
    if (wasActive.current && !active) void Promise.all(['backup-jobs', 'backup-runs', 'dashboard'].map(key => client.invalidateQueries({ queryKey: ['tenant', tenantId, key] })))
    wasActive.current = active
  }, [active, client, tenantId])
  const overlap = actionError instanceof ApiError && actionError.status === 409 && actionError.activeRunId
  return <div className="min-w-0 space-y-5">
    <PageHeader crumbs={[{ label: tx('Yedekleme işleri'), to: `${base}/backup-jobs${listSearch}` }, { label: job.name }]} title={job.name} description={`${job.agentName ?? tx('Adsız bilgisayar')} · ${job.configuration.database}`}
      actions={<Menu.Root><Menu.Trigger className={buttonVariants({ variant: 'outline' })}>{tx('Diğer')}</Menu.Trigger><Menu.Portal><Menu.Positioner sideOffset={4} className="z-50"><Menu.Popup className="rounded-lg border border-border bg-surface-raised p-1 shadow-overlay"><Menu.Item className="cursor-pointer rounded-sm px-3 py-2 text-sm hover:bg-surface-2" onClick={() => setConfirm(true)}>{tx('Korunan veriyi kaldır…')}</Menu.Item></Menu.Popup></Menu.Positioner></Menu.Portal></Menu.Root>} />
    <div role="status" className="space-y-1">
      {status && <p className="font-medium"><StatusPill level={jobStatusLevel[status]}>{jobStatusLabel(status, lastRun.data?.attempts.at(-1)?.progressPercent)}</StatusPill></p>}
      {reason && <p className="text-sm text-text-muted">{reason}</p>}
    </div>
    {(health.isError || timeline.isError || agents.isError || successes.isError) && <ErrorNotice title={tx('Sağlık bilgisi alınamadı.')} error={health.error ?? timeline.error ?? agents.error ?? successes.error} />}
    {actionError !== null && (overlap
      ? <Notice title={tx('Zaten sürüyor')} action={<Link className={linkClass} to={`${base}/backup-runs/${overlap}`}>{tx('Süren denemeyi aç')}</Link>}>{tx('Başka bir yedekleme denemesi sürdüğü için yenisi başlatılmadı.')}</Notice>
      : <ErrorNotice title={tx('İşlem tamamlanamadı')} error={actionError} />)}
    {startedRun && !(job.lastRun?.id === startedRun && !active) && <Notice tone="success" title={tx('Yedekleme denemesi başlatıldı')}><Link className={linkClass} to={`${base}/backup-runs/${startedRun}`}>{tx('Denemeyi aç')}</Link></Notice>}
    <div className="flex flex-wrap gap-2">
      <Button disabled={run.isPending || !job.enabled || job.agentRevoked} onClick={event => { if (event.detail > 1) return; setActionError(null); setStartedRun(undefined); void run.run(job.id).then(result => { if (result) setStartedRun(result.id) }).catch(setActionError) }}>{tx('Şimdi yedekle')}</Button>
      {job.schedule && <Button variant="outline" disabled={toggle.isPending || job.agentRevoked} onClick={() => { setActionError(null); toggle.mutate({ id: job.id, enabled: !job.enabled }, { onError: setActionError }) }}>{job.enabled ? tx('Zamanlamayı durdur') : tx('Zamanlamayı sürdür')}</Button>}
      {offline && <Link className={buttonVariants({ variant: 'outline' })} to={`${base}/agents/${job.agentId}`}>{tx('Bilgisayarı kontrol et')}</Link>}
      {!offline && reasons.includes('capacity_critical') && <Link className={buttonVariants({ variant: 'outline' })} to={`${base}/storage`}>{tx('Depolamayı yönet')}</Link>}
    </div>
    <JobSections job={job} />
    <AgentLogs tenantId={tenantId} jobId={job.id} />
    <Link className={`inline-block text-sm ${linkClass}`} to={`${base}/backup-runs?jobId=${encodeURIComponent(job.id)}`}>{tx('Tüm denemeler')}</Link>
    <ConfirmDialog open={confirm} onClose={() => setConfirm(false)} title={tx('Korunan veriyi kaldır…')} name={job.name} pending={archive.isPending} error={archive.error} confirmLabel={tx('Korunan veriyi kaldır')} onConfirm={() => archive.mutate()} consequence={{ what: tx('Yedekleriniz silinmez; bu iş artık çalışmaz ve listede görünmez.'), who: job.name, reversible: tx('Bu işlem geri alınamaz.') }} />
  </div>
}


/** `…/backup-jobs/:id`: one backup job on its own page. */
export function BackupJobDetailPage() {
  useLanguage()
  const { id } = useParams()
  const { tenantId } = useTenant()
  const jobs = useBackupJobs()
  const job = jobs.data?.find(item => item.id === id)
  return <QueryState isPending={jobs.isPending} error={jobs.error}>
    {job ? <JobDetail key={job.id} job={job} />
      : <EmptyState message={tx('Yedekleme işi bulunamadı.')} action={<Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-jobs`}>{tx('Yedekleme işlerine git')}</Link>} />}
  </QueryState>
}
