import { tx, useLanguage } from '@/lib/i18n'
import { useParams, useSearchParams } from 'react-router-dom'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useDiscoveryInstance } from '@/features/discovery/hooks'
import { selectableNames } from '@/features/discovery/job-prefill'
import { useBackupJob } from '@/features/backup-jobs/hooks'
import { BackupHealthCard } from '@/features/backup-jobs/health-card'
import { JobForm } from '@/features/backup-jobs/job-form'
import { AccessDenied } from '@/components/auth/access-denied'
import { errorMessage } from '@/lib/api/failure-messages'

export function BackupJobWizardPage() {
  useLanguage()
  const { id = '' } = useParams()
  const { tenantId, canOperate } = useTenant()
  const job = useBackupJob(id)
  const [params] = useSearchParams()
  const discoveryInstance = id ? '' : params.get('discoveryInstance') ?? ''
  const discovered = useDiscoveryInstance(discoveryInstance)
  const prefill = discovered.data ? { instance: discovered.data, databases: selectableNames(discovered.data, params.getAll('database')) } : undefined
  const readOnly = Boolean(id) && !canOperate
  const crumbs = [{ label: tx('Yedekleme işleri'), to: `/tenants/${tenantId}/backup-jobs` }, { label: id ? tx(readOnly ? 'Yedekleme sağlığı' : 'Düzenle') : tx('Yeni iş') }]
  let body
  if (!canOperate) body = readOnly ? <p className="text-sm text-text-muted">{tx('Salt okunur')}</p> : <AccessDenied what={tx("Bu sayfada değişiklik yapma yetkiniz yok. Viewer rolü salt okunurdur.")} />
  else if (id) body = <QueryState isPending={job.isPending} error={job.error}>{job.data && <JobForm key={`${tenantId}:${id}`} job={job.data} />}</QueryState>
  else if (discoveryInstance && discovered.isPending) body = <QueryState isPending error={null} />
  else body = <>
    {discovered.error && <Notice tone="warning" title={tx("Keşfedilen veritabanları yüklenemedi")}>{errorMessage(discovered.error)}{' '}{tx("Kaynağı elle girebilirsiniz.")}</Notice>}
    <JobForm key={`${tenantId}:new`} prefill={prefill} />
  </>
  return <div className="space-y-6">
    <PageHeader crumbs={crumbs} title={readOnly ? tx('Yedekleme sağlığı') : id ? tx('Yedekleme işini düzenle') : tx('Yeni yedekleme işi')} description={readOnly ? job.data?.name : tx("Kaynak, depolama, zamanlama ve saklama ayarlarını adım adım belirleyin.")} />
    {id && <BackupHealthCard key={`${tenantId}:${id}`} jobId={id} />}
    {body}
  </div>
}
