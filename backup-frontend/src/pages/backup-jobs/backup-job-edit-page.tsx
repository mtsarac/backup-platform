import { tx, useLanguage } from '@/lib/i18n'
import { useParams } from 'react-router-dom'
import { PageHeader } from '@/components/ui/page-header'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useBackupJob, useToggleBackupJob } from '@/features/backup-jobs/hooks'
import { BackupHealthCard } from '@/features/backup-jobs/health-card'
import { JobDetails } from '@/features/backup-jobs/job-details'
import { JobEditForm } from '@/features/backup-jobs/job-edit-form'

/** `…/backup-jobs/:id/edit`: health and one single-page form; Viewers get the health and the technical details read-only. */
export function BackupJobEditPage() {
  useLanguage()
  const { id = '' } = useParams()
  const { tenantId, canOperate } = useTenant()
  const job = useBackupJob(id)
  const toggle = useToggleBackupJob()
  const readOnly = !canOperate
  const source = job.data?.configuration
  const paths = source?.files?.paths
  const sourceSummary = source && [
    source.engine === 'file-system' ? tx('Dosya / Klasör') : source.engine === 'mssql' ? 'Microsoft SQL Server' : 'PostgreSQL',
    source.database,
    source.engine === 'file-system' && paths?.length ? paths.every(path => path === '.') ? tx('Tüm kaynak') : paths.join(', ') : null,
  ].filter(Boolean).join(' · ')
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Yedekleme işleri'), to: `/tenants/${tenantId}/backup-jobs` }, { label: readOnly ? tx('Yedekleme sağlığı') : tx('Düzenle') }]}
      title={job.data?.name ?? (readOnly ? tx('Yedekleme sağlığı') : tx('Yedekleme işini düzenle'))}
      description={job.data && [job.data.agentName, sourceSummary].filter(Boolean).join(' · ')}
      meta={job.data && <><span className="text-sm text-text-muted">{tx('İş durumu')}:</span><StatusPill status={job.data.enabled ? 'Active' : 'Disabled'} /></>}
      actions={job.data && canOperate && <div className="space-y-1"><Button type="button" variant="outline" disabled={toggle.isPending} onClick={() => toggle.mutate({ id: job.data!.id, enabled: !job.data!.enabled })}>{toggle.isPending ? tx('İşleniyor…') : job.data.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button><p className="text-xs text-text-muted">{tx("İş durumu hemen değişir; Kaydet'e basmak gerekmez.")}</p></div>} />
    {toggle.isError && <ErrorNotice title={tx('İşlem tamamlanamadı')} error={toggle.error} />}
    <BackupHealthCard key={`${tenantId}:${id}`} jobId={id} />
    {readOnly ? <>{job.data && <JobDetails job={job.data} readOnly />}<p className="text-sm text-text-muted">{tx('Salt okunur')}</p></>
      : <QueryState isPending={job.isPending} error={job.error}>{job.data && <JobEditForm key={`${tenantId}:${id}`} job={job.data} />}</QueryState>}
    {!readOnly && job.data && <JobDetails job={job.data} readOnly={false} />}
  </div>
}
