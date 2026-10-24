import { Activity, Bell, Database, DatabaseBackup, Gauge, HardDrive, KeyRound, LayoutDashboard, Monitor, Radar, RotateCcw, ShieldCheck, Upload } from 'lucide-react'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { SidebarFrame, type NavGroup } from './nav'
import { tx, useLanguage } from '@/lib/i18n'

export function Sidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  const { tenantId, canAdmin } = useTenant()
  const { user } = useAuth()
  useLanguage()
  const base = `/tenants/${tenantId}`
  const groups: NavGroup[] = [
    { items: [{ label: tx("Ana Sayfa"), to: base, icon: LayoutDashboard, end: true }] },
    { heading: tx("Yedeklerim"), items: [
      { label: tx("Yedekleme işleri"), to: `${base}/backup-jobs`, icon: Database },
      { label: tx("Yedekleme denemeleri"), to: `${base}/backup-runs`, icon: Activity },
      { label: tx("Yedek dosyaları"), to: `${base}/artifacts`, icon: HardDrive },
      { label: tx("Geri yüklemeler"), to: `${base}/restores`, icon: RotateCcw },
    ] },
    { heading: tx("Bilgisayarlarım ve Depolama"), items: [
      { label: tx("Yedekleme bilgisayarları"), to: `${base}/agents`, icon: Monitor },
      { label: tx("Veritabanı bulma"), to: `${base}/discovery`, icon: Radar },
      { label: tx("Depolama alanları"), to: `${base}/storage`, icon: Gauge },
      { label: tx("Yükleme bant genişliği"), to: `${base}/upload-bandwidth`, icon: Upload },
    ] },
    ...(canAdmin ? [{ heading: tx("Ayarlar"), items: [
      { label: tx("Bildirimler"), to: `${base}/notifications`, icon: Bell },
      { label: tx("Şifreleme"), to: `${base}/encryption`, icon: KeyRound },
      { label: tx("Destek erişimi"), to: `${base}/support-access`, icon: ShieldCheck },
    ] }] : []),
    ...(user?.isPlatformAdmin ? [{ heading: tx("Platform"), items: [{ label: tx("Platform Yönetimi"), to: '/platform', icon: ShieldCheck }] }] : []),
  ]
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel={tx("Ana menü")} label={tx("Ana gezinme")} brand={{ icon: DatabaseBackup, title: tx("Yedekleme Platformu"), caption: tx("Yönetim Konsolu") }} groups={groups} />
}
