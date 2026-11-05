import { tx, useLanguage } from '@/lib/i18n'
import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { PageHeader } from '@/components/ui/page-header'
import { RouteTabs } from '@/components/ui/tabs'
import { platformCrumb } from '@/features/platform/crumbs'
import { platformPaths } from '@/features/platform/paths'

/** Platform storage hub: how much room each workspace uses (usage) and where backups are written (destinations). */
export function PlatformStorageLayout() {
  useLanguage()
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Depolama")} description={tx("Çalışma alanlarının depolama kullanımı ve yedeklerin yazıldığı depolama hedefleri.")} />
    <RouteTabs label={tx("Depolama bölümleri")} items={[
      { to: platformPaths.storage, label: tx('Kullanım'), end: true },
      { to: platformPaths.storageConnections, label: tx('Hedefler') },
    ]} />
    <Outlet />
  </div>
}

/** The destinations list used to live at /platform/storage-connections; bookmarks and filters carry over. */
export function PlatformStorageConnectionsRedirect() {
  const { search } = useLocation()
  return <Navigate replace to={`${platformPaths.storageConnections}${search}`} />
}
