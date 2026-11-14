import { tx, useLanguage } from '@/lib/i18n'
import { useSearchParams } from 'react-router-dom'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useDiscoveryInstance } from '@/features/discovery/hooks'
import { selectableNames } from '@/features/discovery/job-prefill'
import { JobWizard } from '@/features/backup-jobs/job-wizard'
import { AccessDenied } from '@/components/auth/access-denied'

/** `…/backup-jobs/new`: the create wizard, optionally prefilled from `?discoveryInstance=…&database=…`. */
export function BackupJobWizardPage() {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const [params] = useSearchParams()
  const discoveryInstance = params.get('discoveryInstance') ?? ''
  const discovered = useDiscoveryInstance(discoveryInstance)
  const prefill = discovered.data ? { instance: discovered.data, databases: selectableNames(discovered.data, params.getAll('database')) } : undefined
  let body
  if (!canOperate) body = <AccessDenied what={tx("Bu sayfada değişiklik yapma yetkiniz yok. Viewer rolü salt okunurdur.")} />
  else if (discoveryInstance && discovered.isPending) body = <QueryState isPending error={null} />
  else body = <>
    {discovered.error && <ErrorNotice title={tx("Keşfedilen veritabanları yüklenemedi")} error={discovered.error} onRetry={() => void discovered.refetch()} />}
    <JobWizard key={`${tenantId}:new`} prefill={prefill} />
  </>
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Yedekleme işleri'), to: `/tenants/${tenantId}/backup-jobs` }, { label: tx('Yeni iş') }]} title={tx('Yeni yedekleme işi')} description={tx("Kaynak, depolama, zamanlama ve koruma ayarlarını adım adım belirleyin.")} />
    {body}
  </div>
}
