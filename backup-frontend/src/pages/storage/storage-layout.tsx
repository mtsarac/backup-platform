import { tx, useLanguage } from '@/lib/i18n'
import { Link, Outlet } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button-variants'
import { PageHeader } from '@/components/ui/page-header'
import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** Storage hub: where backups go (connections), how much room there is (usage and quota), and what each provider can do. */
export function StorageLayout() {
  useLanguage()
  const { tenantId, tenant, canAdmin } = useTenant()
  const base = `/tenants/${tenantId}/storage`
  return <>
    <PageHeader title={tx("Storage")} description={tx("{0} · Depolama hedefleri, kullanım ve kota.", { "0": tenant.name })}
      actions={canAdmin ? <Link to={`${base}/connections/new`} className={buttonVariants()}><Plus aria-hidden="true" className="size-4" />{tx("Bağlantı ekle")}</Link> : undefined} />
    <RouteTabs label={tx("Storage bölümleri")} items={[{ to: base, label: tx('Bağlantılar'), end: true }, { to: `${base}/usage`, label: tx('Kullanım ve kota') }, { to: `${base}/providers`, label: tx('Sağlayıcılar') }]} />
    <Outlet />
  </>
}
