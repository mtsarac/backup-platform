import { useEffect, useRef } from 'react'
import { Activity, Database, HardDrive, LayoutDashboard, Monitor, KeyRound, ShieldCheck, X } from 'lucide-react'
import { NavLink } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'

const items = [
  { label: 'Dashboard', path: '', icon: LayoutDashboard },
  { label: 'Agents', path: '/agents', icon: Monitor },
  { label: 'Backup Jobs', path: '/backup-jobs', icon: Database },
  { label: 'Backup Runs', path: '/backup-runs', icon: Activity },
  { label: 'Artifacts', path: '/artifacts', icon: HardDrive },
]
function SidebarContent({ onClose }: { onClose?: () => void }) {
  const { tenantId, canAdmin } = useTenant()
  const { user } = useAuth()
  return <>
    <div className="flex h-14 items-center justify-between border-b border-border px-4">
      <div className="flex items-center gap-2"><div className="flex size-7 items-center justify-center rounded bg-primary text-primary-foreground"><HardDrive className="size-4" /></div><span className="font-semibold text-text-heading">Backup Yönetimi</span></div>
      {onClose && <Button variant="ghost" size="icon" onClick={onClose} aria-label="Menüyü kapat"><X className="size-5" /></Button>}
    </div>
    <nav aria-label="Ana navigation" className="flex-1 overflow-auto p-3"><div className="mb-1 px-3 text-xs font-medium text-text-muted">Yönetim</div>
      <ul className="space-y-0.5">{[...items, ...(canAdmin ? [{ label: 'Enrollment', path: '/enrollment', icon: KeyRound }] : [])].map(item => <li key={item.path}>
        <NavLink to={`/tenants/${tenantId}${item.path}`} end={!item.path} onClick={onClose} className={({ isActive }) => `flex items-center gap-3 rounded-md px-3 py-2 text-sm ${isActive ? 'border-l-2 border-primary bg-accent/50 font-medium text-accent-foreground' : 'text-text hover:bg-surface-2'}`}><item.icon className="size-4" />{item.label}</NavLink>
      </li>)}</ul>
      {user?.isPlatformAdmin && <><div className="mb-1 mt-4 px-3 text-xs font-medium text-text-muted">Platform</div>
        <ul><li><NavLink to="/platform" onClick={onClose} className="flex items-center gap-3 rounded-md px-3 py-2 text-sm text-text hover:bg-surface-2"><ShieldCheck className="size-4" />Platform Administration</NavLink></li></ul></>}
    </nav>
  </>
}
export function Sidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    if (mobileOpen && !dialog.current?.open) dialog.current?.showModal()
    if (!mobileOpen && dialog.current?.open) dialog.current.close()
  }, [mobileOpen])
  return <>
    <aside className="fixed inset-y-0 left-0 z-20 hidden w-60 flex-col border-r border-border bg-surface lg:flex"><SidebarContent /></aside>
    <dialog ref={dialog} aria-label="Ana menü" onCancel={onMobileClose} onClose={onMobileClose} className="fixed inset-y-0 left-0 m-0 h-svh max-h-none w-60 border-r border-border bg-surface p-0 text-text backdrop:bg-black/50"><SidebarContent onClose={onMobileClose} /></dialog>
  </>
}
