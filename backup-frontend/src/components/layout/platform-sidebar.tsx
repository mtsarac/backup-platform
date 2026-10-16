import { tx, useLanguage } from '@/lib/i18n'
import { ArrowLeft, Bell, Building2, Gauge, HardDrive, Layers, LayoutDashboard, Monitor, ScrollText, ShieldCheck, Sliders, Users } from 'lucide-react'
import { platformPaths } from '@/features/platform/paths'
import { SidebarFrame, type NavGroup } from './nav'

const groups: NavGroup[] = [
  { items: [{ get label() { return tx("Dashboard") }, to: platformPaths.root, icon: LayoutDashboard, end: true }] },
  { get heading() { return tx("Müşteriler") }, items: [
    { get label() { return tx("Tenants") }, to: platformPaths.tenants, icon: Building2 },
    { get label() { return tx("Users") }, to: platformPaths.users, icon: Users },
    { get label() { return tx("Plans") }, to: platformPaths.plans, icon: Layers },
    { get label() { return tx("Agents") }, to: platformPaths.agents(), icon: Monitor },
  ] },
  { get heading() { return tx("Depolama") }, items: [
    { get label() { return tx("Storage Usage") }, to: platformPaths.storage, icon: Gauge },
    { get label() { return tx("Storage Targets") }, to: platformPaths.storageConnections, icon: HardDrive },
    { get label() { return tx("Quota Policies") }, to: platformPaths.quotaPolicies, icon: Sliders },
  ] },
  { get heading() { return tx("Operasyon") }, items: [
    { get label() { return tx("Bildirim altyapısı") }, to: platformPaths.notificationTransports, icon: Bell },
    { get label() { return tx("Support Sessions") }, to: platformPaths.supportSessions, icon: ShieldCheck },
    { get label() { return tx("Audit") }, to: platformPaths.audit(), icon: ScrollText },
  ] },
  { items: [{ get label() { return tx("Tenant arayüzüne dön") }, to: '/', icon: ArrowLeft, end: true }] },
]

export function PlatformSidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  useLanguage()
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel="Platform menüsü" label={tx("Platform navigation")} brand={{ icon: ShieldCheck, title: tx('Platform Administration'), caption: tx('Tüm müşteriler') }} groups={groups} />
}
