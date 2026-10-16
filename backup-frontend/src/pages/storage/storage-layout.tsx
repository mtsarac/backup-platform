import { Link, Outlet } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button-variants'
import { PageHeader } from '@/components/ui/page-header'
import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** Storage hub: where backups go (connections), how much room there is (usage and quota), and what each provider can do. */
export function StorageLayout() {
  const { tenantId, tenant, canAdmin } = useTenant()
  const base = `/tenants/${tenantId}/storage`
  return <>
    <PageHeader title="Storage" description={`${tenant.name} · Depolama hedefleri, kullanım ve kota.`}
      actions={canAdmin ? <Link to={`${base}/connections/new`} className={buttonVariants()}><Plus aria-hidden="true" className="size-4" />Bağlantı ekle</Link> : undefined} />
    <RouteTabs label="Storage bölümleri" items={[{ to: base, label: 'Bağlantılar', end: true }, { to: `${base}/usage`, label: 'Kullanım ve kota' }, { to: `${base}/providers`, label: 'Sağlayıcılar' }]} />
    <Outlet />
  </>
}
