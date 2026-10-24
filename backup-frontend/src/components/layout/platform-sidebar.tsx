import { tx, useLanguage } from '@/lib/i18n'
import { ArrowLeft, Bell, Building2, CreditCard, Gauge, HardDrive, KeyRound, LayoutDashboard, Monitor, ScrollText, ShieldCheck, Sliders, Users } from 'lucide-react'
import { platformPaths } from '@/features/platform/paths'
import { SidebarFrame, type NavGroup } from './nav'

export function PlatformSidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  useLanguage()
  const groups: NavGroup[] = [
    { heading: tx("Genel bakış"), items: [{ label: tx("Platform özeti"), to: platformPaths.root, icon: LayoutDashboard, end: true }] },
    { heading: tx("Müşteriler"), items: [
      { label: tx("Çalışma alanları"), to: platformPaths.tenants, icon: Building2 },
      { label: tx("Kullanıcılar"), to: platformPaths.users, icon: Users },
      { label: tx("Planlar"), to: platformPaths.plans, icon: CreditCard },
    ] },
    { heading: tx("Altyapı"), items: [
      { label: tx("Depolama kullanımı"), to: platformPaths.storage, icon: Gauge },
      { label: tx("Depolama hedefleri"), to: platformPaths.storageConnections, icon: HardDrive },
      { label: tx("Yedekleme bilgisayarları"), to: platformPaths.agents(), icon: Monitor },
      { label: tx("Kota ilkeleri"), to: platformPaths.quotaPolicies, icon: Sliders },
    ] },
    { heading: tx("Güvenlik ve destek"), items: [
      { label: tx("Destek oturumları"), to: platformPaths.supportSessions, icon: ShieldCheck },
      { label: tx("Acil destek erişimi"), to: platformPaths.breakGlass, icon: KeyRound },
      { label: tx("Denetim kaydı"), to: platformPaths.audit(), icon: ScrollText },
      { label: tx("Bildirim altyapısı"), to: platformPaths.notificationTransports, icon: Bell },
    ] },
    { items: [{ label: tx("Çalışma alanı arayüzüne dön"), to: '/', icon: ArrowLeft, end: true }] },
  ]
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel={tx("Platform menüsü")} label={tx("Platform gezinme menüsü")} brand={{ icon: ShieldCheck, title: tx("Platform Yönetimi"), caption: tx("Tüm müşteriler") }} groups={groups} />
}
