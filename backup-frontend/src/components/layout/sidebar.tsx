import { Activity, Bell, Database, DatabaseBackup, Gauge, HardDrive, LayoutDashboard, Monitor, Radar, RotateCcw, ShieldCheck, Upload } from 'lucide-react'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { SidebarFrame, type NavGroup } from './nav'

export function Sidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  const { tenantId, canAdmin } = useTenant()
  const { user } = useAuth()
  const base = `/tenants/${tenantId}`
  const groups: NavGroup[] = [
    { items: [{ label: 'Dashboard', to: base, icon: LayoutDashboard, end: true }] },
    { heading: 'Yedekleme', items: [
      { label: 'Backup Jobs', to: `${base}/backup-jobs`, icon: Database },
      { label: 'Backup Runs', to: `${base}/backup-runs`, icon: Activity },
      { label: 'Artifacts', to: `${base}/artifacts`, icon: HardDrive },
      { label: 'Restores', to: `${base}/restores`, icon: RotateCcw },
    ] },
    { heading: 'Altyapı', items: [
      { label: 'Agents', to: `${base}/agents`, icon: Monitor },
      { label: 'Keşfedilen Veritabanları', to: `${base}/discovery`, icon: Radar },
      { label: 'Storage', to: `${base}/storage`, icon: Gauge },
      { label: 'Upload Bandwidth', to: `${base}/upload-bandwidth`, icon: Upload },
    ] },
    ...(canAdmin ? [{ heading: 'Çalışma alanı', items: [
      { label: 'Bildirimler', to: `${base}/notifications`, icon: Bell },
      { label: 'Support Access', to: `${base}/support-access`, icon: ShieldCheck },
    ] }] : []),
    ...(user?.isPlatformAdmin ? [{ heading: 'Platform', items: [{ label: 'Platform Administration', to: '/platform', icon: ShieldCheck }] }] : []),
  ]
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel="Ana menü" label="Ana navigation" brand={{ icon: DatabaseBackup, title: 'Backup Platform', caption: 'Yönetim konsolu' }} groups={groups} />
}
