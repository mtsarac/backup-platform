import { Bell, Database, DatabaseBackup, Gauge, KeyRound, LayoutDashboard, Monitor, Radar, Share2, ShieldCheck, Upload } from 'lucide-react'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { AccountLink } from './account-link'
import { SidebarFrame, type NavGroup, type NavItem } from './nav'
import { tx, useLanguage } from '@/lib/i18n'

/** Menu visibility only; the pages and the API enforce access themselves. */
type GatedItem = NavItem & { admin?: boolean }

export function Sidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  const { tenantId, canAdmin } = useTenant()
  const { user } = useAuth()
  useLanguage()
  const base = `/tenants/${tenantId}`
  const config: { heading?: string; items: GatedItem[] }[] = [
    { items: [
      { label: tx("Genel Bakış"), to: base, icon: LayoutDashboard, end: true },
      { label: tx("Yedeklemeler"), to: `${base}/backup-jobs`, icon: Database, activeFor: ['backup-jobs', 'backup-runs', 'artifacts', 'restores'].map((path) => `${base}/${path}`) },
      { label: tx("Paylaşımlarım"), to: `${base}/shares`, icon: Share2 },
    ] },
    { heading: tx("Altyapı"), items: [
      { label: tx("Yedekleme bilgisayarları"), to: `${base}/agents`, icon: Monitor },
      { label: tx("Veritabanı bulma"), to: `${base}/discovery`, icon: Radar },
      { label: tx("Depolama"), to: `${base}/storage`, icon: Gauge },
    ] },
    { heading: tx("Ayarlar"), items: [
      { label: tx("Bildirimler"), to: `${base}/notifications`, icon: Bell, admin: true },
      { label: tx("Şifreleme"), to: `${base}/encryption`, icon: KeyRound, admin: true },
      { label: tx("Yükleme bant genişliği"), to: `${base}/upload-bandwidth`, icon: Upload },
    ] },
    ...(user?.role === 'PlatformAdmin' ? [{ heading: tx("Platform"), items: [{ label: tx("Platform Yönetimi"), to: '/platform', icon: ShieldCheck }] }] : []),
  ]
  const groups: NavGroup[] = config.map(({ heading, items }) => ({ heading, items: items.filter((item) => canAdmin || !item.admin) }))
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel={tx("Ana menü")} label={tx("Ana gezinme")} brand={{ icon: DatabaseBackup, title: tx("Yedekleme Platformu"), caption: tx("Yönetim Konsolu") }} groups={groups} footer={<AccountLink />} />
}
