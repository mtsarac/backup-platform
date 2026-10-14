import { useEffect, useRef } from 'react'
import { ArrowLeft, LayoutDashboard, Building2, Users, Monitor, Layers, ScrollText, ShieldCheck, Gauge, HardDrive, Sliders, Bell, X } from 'lucide-react'
import { NavLink } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { platformPaths } from '@/features/platform/paths'

const items = [
  { label: 'Dashboard', path: platformPaths.root, icon: LayoutDashboard, end: true },
  { label: 'Tenants', path: platformPaths.tenants, icon: Building2, end: false },
  { label: 'Storage Usage', path: platformPaths.storage, icon: Gauge, end: false },
  { label: 'Storage Targets', path: platformPaths.storageConnections, icon: HardDrive, end: false },
  { label: 'Bildirim altyapısı', path: platformPaths.notificationTransports, icon: Bell, end: false },
  { label: 'Quota Policies', path: platformPaths.quotaPolicies, icon: Sliders, end: false },
  { label: 'Plans', path: platformPaths.plans, icon: Layers, end: false },
  { label: 'Users', path: platformPaths.users, icon: Users, end: false },
  { label: 'Agents', path: platformPaths.agents(), icon: Monitor, end: false },
  { label: 'Support Sessions', path: platformPaths.supportSessions, icon: ShieldCheck, end: false },
  { label: 'Audit', path: platformPaths.audit(), icon: ScrollText, end: false },
]

function PlatformSidebarContent({ onClose }: { onClose?: () => void }) {
  return <>
    <div className="flex h-14 items-center justify-between border-b border-border px-4">
      <div className="flex items-center gap-2"><div className="flex size-7 items-center justify-center rounded bg-primary text-primary-foreground"><ShieldCheck className="size-4" /></div><span className="font-semibold text-text-heading">Platform Administration</span></div>
      {onClose && <Button variant="ghost" size="icon" onClick={onClose} aria-label="Menüyü kapat"><X /></Button>}
    </div>
    <nav aria-label="Platform navigation" className="flex-1 overflow-auto p-3">
      <ul className="space-y-0.5">{items.map(item => <li key={item.path}>
        <NavLink to={item.path} end={item.end} onClick={onClose} className={({ isActive }) => `flex items-center gap-3 rounded-md px-3 min-h-12 text-base ${isActive ? 'border-l-2 border-primary bg-accent/50 font-medium text-accent-foreground' : 'text-text hover:bg-surface-2'}`}><item.icon className="size-6" />{item.label}</NavLink>
      </li>)}</ul>
      <div className="mt-4 border-t border-border pt-3"><NavLink to="/" onClick={onClose} className="flex items-center gap-3 rounded-md px-3 min-h-12 text-base text-text hover:bg-surface-2"><ArrowLeft className="size-6" />Tenant arayüzüne dön</NavLink></div>
    </nav>
  </>
}

export function PlatformSidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    if (mobileOpen && !dialog.current?.open) dialog.current?.showModal()
    if (!mobileOpen && dialog.current?.open) dialog.current.close()
  }, [mobileOpen])
  return <>
    <aside className="fixed inset-y-0 left-0 z-20 hidden w-60 flex-col border-r border-border bg-surface lg:flex"><PlatformSidebarContent /></aside>
    <dialog ref={dialog} aria-label="Platform menüsü" onCancel={onMobileClose} onClose={onMobileClose} className="fixed inset-y-0 left-0 m-0 h-svh max-h-none w-60 border-r border-border bg-surface p-0 text-text backdrop:bg-black/50"><PlatformSidebarContent onClose={onMobileClose} /></dialog>
  </>
}
