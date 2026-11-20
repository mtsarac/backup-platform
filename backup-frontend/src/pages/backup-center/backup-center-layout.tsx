import { tx, useLanguage } from '@/lib/i18n'
import { Link, Outlet, useMatch } from 'react-router-dom'
import { buttonVariants } from '@/components/ui/button-variants'
import { PageHeader } from '@/components/ui/page-header'
import { RouteTabs } from '@/components/ui/tabs'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'

/** The create button sits in the heading; a tenant without jobs gets it from the empty list instead. */
function CreateJobAction({ to }: { to: string }) {
  useLanguage()
  const jobs = useBackupJobs()
  return jobs.data?.length ? <Link className={buttonVariants()} to={to}>{tx('Yedekleme işi oluştur')}</Link> : null
}

/** One heading and four route tabs for the Backup Job, Backup Run, Artifact and Restore lists; each tab owns its filters, columns and actions. Detail and wizard routes stay outside it. */
export function BackupCenterLayout() {
  useLanguage()
  const { tenantId } = useTenant()
  const base = `/tenants/${tenantId}`
  const onJobs = useMatch(`${base}/backup-jobs`)
  return <div className="space-y-6">
    <PageHeader title={tx("Yedeklemeler")} actions={onJobs && <CreateJobAction to={`${base}/backup-jobs/new`} />} />
    <RouteTabs label={tx("Yedekleme bölümleri")} items={[
      { to: `${base}/backup-jobs`, label: tx('Yedekleme işleri') },
      { to: `${base}/backup-runs`, label: tx('Geçmiş') },
      { to: `${base}/restores`, label: tx('Geri yüklemeler') },
      { to: `${base}/backup-shares`, label: tx('Paylaşımlar') },
    ]} />
    <Outlet />
  </div>
}
