import { tx, useLanguage } from '@/lib/i18n'
import { useParams } from 'react-router-dom'
import { PageHeader } from '@/components/ui/page-header'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useBackupJob } from '@/features/backup-jobs/hooks'
import { BackupHealthCard } from '@/features/backup-jobs/health-card'
import { JobDetails } from '@/features/backup-jobs/job-details'
import { JobEditForm } from '@/features/backup-jobs/job-edit-form'

/** `…/backup-jobs/:id/edit`: health and one single-page form; Viewers get the health and the technical details read-only. */
export function BackupJobEditPage() {
  useLanguage()
  const { id = '' } = useParams()
  const { tenantId, canOperate } = useTenant()
  const job = useBackupJob(id)
  const readOnly = !canOperate
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Yedekleme işleri'), to: `/tenants/${tenantId}/backup-jobs` }, { label: readOnly ? tx('Yedekleme sağlığı') : tx('Düzenle') }]}
      title={readOnly ? tx('Yedekleme sağlığı') : tx('Yedekleme işini düzenle')}
      description={readOnly ? job.data?.name : tx("Tüm ayarlar bu sayfada; değişiklikler Kaydet'e basana kadar uygulanmaz.")} />
    <BackupHealthCard key={`${tenantId}:${id}`} jobId={id} />
    {job.data && <JobDetails job={job.data} readOnly={readOnly} />}
    {readOnly ? <p className="text-sm text-text-muted">{tx('Salt okunur')}</p>
      : <QueryState isPending={job.isPending} error={job.error}>{job.data && <JobEditForm key={`${tenantId}:${id}`} job={job.data} />}</QueryState>}
  </div>
}
