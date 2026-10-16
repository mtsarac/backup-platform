import { ArrowLeft, Bell, Building2, Gauge, HardDrive, Layers, LayoutDashboard, Monitor, ScrollText, ShieldCheck, Sliders, Users } from 'lucide-react'
import { platformPaths } from '@/features/platform/paths'
import { SidebarFrame, type NavGroup } from './nav'

const groups: NavGroup[] = [
  { items: [{ label: 'Dashboard', to: platformPaths.root, icon: LayoutDashboard, end: true }] },
  { heading: 'Müşteriler', items: [
    { label: 'Tenants', to: platformPaths.tenants, icon: Building2 },
    { label: 'Users', to: platformPaths.users, icon: Users },
    { label: 'Plans', to: platformPaths.plans, icon: Layers },
    { label: 'Agents', to: platformPaths.agents(), icon: Monitor },
  ] },
  { heading: 'Depolama', items: [
    { label: 'Storage Usage', to: platformPaths.storage, icon: Gauge },
    { label: 'Storage Targets', to: platformPaths.storageConnections, icon: HardDrive },
    { label: 'Quota Policies', to: platformPaths.quotaPolicies, icon: Sliders },
  ] },
  { heading: 'Operasyon', items: [
    { label: 'Bildirim altyapısı', to: platformPaths.notificationTransports, icon: Bell },
    { label: 'Support Sessions', to: platformPaths.supportSessions, icon: ShieldCheck },
    { label: 'Audit', to: platformPaths.audit(), icon: ScrollText },
  ] },
  { items: [{ label: 'Tenant arayüzüne dön', to: '/', icon: ArrowLeft, end: true }] },
]

export function PlatformSidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel="Platform menüsü" label="Platform navigation" brand={{ icon: ShieldCheck, title: 'Platform Administration', caption: 'Tüm müşteriler' }} groups={groups} />
}
