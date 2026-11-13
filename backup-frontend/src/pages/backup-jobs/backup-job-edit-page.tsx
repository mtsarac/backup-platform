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
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Yedekleme işleri'), to: `/tenants/${tenantId}/backup-jobs` }, { label: readOnly ? tx('Yedekleme sağlığı') : tx('Düzenle') }]}
      title={readOnly ? tx('Yedekleme sağlığı') : tx('Yedekleme işini düzenle')}
      description={readOnly ? job.data?.name : tx("Form değişiklikleri Kaydet'e basana kadar uygulanmaz. İş durumunu değiştirmek hemen uygulanır ve kaydedilmemiş form değişikliklerini etkilemez.")}
      meta={job.data && <><span className="text-sm text-text-muted">{tx('İş durumu')}:</span><StatusPill status={job.data.enabled ? 'Active' : 'Disabled'} /></>}
      actions={job.data && canOperate && <Button type="button" variant="outline" disabled={toggle.isPending} onClick={() => toggle.mutate({ id: job.data!.id, enabled: !job.data!.enabled })}>{toggle.isPending ? tx('İşleniyor…') : job.data.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>} />
    {toggle.isError && <ErrorNotice title={tx('İşlem tamamlanamadı')} error={toggle.error} />}
    <BackupHealthCard key={`${tenantId}:${id}`} jobId={id} />
    {job.data && <JobDetails job={job.data} readOnly={readOnly} />}
    {readOnly ? <p className="text-sm text-text-muted">{tx('Salt okunur')}</p>
      : <QueryState isPending={job.isPending} error={job.error}>{job.data && <JobEditForm key={`${tenantId}:${id}`} job={job.data} />}</QueryState>}
  </div>
}
