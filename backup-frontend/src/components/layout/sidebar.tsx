import { Database, DatabaseBackup, Gauge, LayoutDashboard, Monitor, ShieldCheck, SlidersHorizontal } from 'lucide-react'
import { Link } from 'react-router-dom'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { AccountLink } from './account-link'
import { SidebarFrame, type NavGroup } from './nav'
import { tx, useLanguage } from '@/lib/i18n'

export function Sidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  const { tenantId } = useTenant()
  const { user } = useAuth()
  useLanguage()
  const base = `/tenants/${tenantId}`
  const under = (...paths: string[]) => paths.map((path) => `${base}/${path}`)
  const groups: NavGroup[] = [{ items: [
    { label: tx("Ana sayfa"), to: base, icon: LayoutDashboard, end: true },
    { label: tx("Yedeklemeler"), to: `${base}/backup-jobs`, icon: Database, activeFor: under('backup-jobs', 'backup-runs', 'artifacts', 'restores', 'backup-shares') },
    { label: tx("Bilgisayarlar"), to: `${base}/agents`, icon: Monitor, activeFor: under('enrollment', 'discovery') },
    { label: tx("Depolama"), to: `${base}/storage`, icon: Gauge },
    { label: tx("Ayarlar"), to: `${base}/settings`, icon: SlidersHorizontal, activeFor: under('settings') },
  ] }]
  const footer = <div className="space-y-1">
    {user?.role === 'PlatformAdmin' && <Link to="/platform" className="flex min-h-control items-center gap-3 rounded-lg px-3 text-body text-text transition-colors hover:bg-border"><ShieldCheck aria-hidden="true" className="size-4 shrink-0 text-text-muted" />{tx("Platform yönetimi")}</Link>}
    <AccountLink />
  </div>
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel={tx("Ana menü")} label={tx("Ana gezinme")} brand={{ icon: DatabaseBackup, title: tx("Yedekleme Platformu"), caption: tx("Yönetim Konsolu") }} groups={groups} footer={footer} />
}
