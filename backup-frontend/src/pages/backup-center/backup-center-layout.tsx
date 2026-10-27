import { tx, useLanguage } from '@/lib/i18n'
import { Outlet } from 'react-router-dom'
import { PageHeader } from '@/components/ui/page-header'
import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** One heading and four route tabs for the Backup Job, Backup Run, Artifact and Restore lists; each tab owns its filters, columns and actions. Detail and wizard routes stay outside it. */
export function BackupCenterLayout() {
  useLanguage()
  const { tenantId } = useTenant()
  const base = `/tenants/${tenantId}`
  return <div className="space-y-6">
    <PageHeader title={tx("Yedeklemeler")} />
    <RouteTabs label={tx("Yedekleme bölümleri")} items={[
      { to: `${base}/backup-jobs`, label: tx('Yedekleme işleri') },
      { to: `${base}/backup-runs`, label: tx('Yedekleme denemeleri') },
      { to: `${base}/artifacts`, label: tx('Yedek dosyaları') },
      { to: `${base}/restores`, label: tx('Geri yüklemeler') },
    ]} />
    <Outlet />
  </div>
}
