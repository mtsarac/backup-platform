import { useEffect, useRef, type ComponentType, type ReactNode } from 'react'
import { X } from 'lucide-react'
import { NavLink } from 'react-router-dom'
import { Button } from '@/components/ui/button'

export interface NavItem { label: string; to: string; icon: ComponentType<{ className?: string }>; end?: boolean }
export interface NavGroup { heading?: string; items: readonly NavItem[] }

function Brand({ icon: Icon, title, caption }: { icon: ComponentType<{ className?: string }>; title: string; caption: string }) {
  return <div className="flex items-center gap-3">
    <span className="flex size-9 items-center justify-center rounded-lg bg-primary text-primary-foreground"><Icon aria-hidden="true" className="size-5" /></span>
    <span className="min-w-0 leading-tight"><span className="block font-semibold text-sidebar-foreground">{title}</span><span className="block text-xs text-sidebar-muted">{caption}</span></span>
  </div>
}

function SidebarContent({ brand, label, groups, footer, onClose }: { brand: { icon: ComponentType<{ className?: string }>; title: string; caption: string }; label: string; groups: readonly NavGroup[]; footer?: ReactNode; onClose?: () => void }) {
  return <>
    <div className="flex h-16 shrink-0 items-center justify-between px-5">
      <Brand {...brand} />
      {onClose && <Button variant="ghost" size="icon" onClick={onClose} aria-label="Menüyü kapat" className="text-sidebar-foreground hover:bg-sidebar-active"><X /></Button>}
    </div>
    <nav aria-label={label} className="flex-1 space-y-5 overflow-y-auto px-3 py-3">
      {groups.map((group, index) => <div key={group.heading ?? index}>
        {group.heading && <p className="mb-1 px-3 text-xs font-medium text-sidebar-muted">{group.heading}</p>}
        <ul className="space-y-0.5">{group.items.map((item) => <li key={item.to}>
          <NavLink to={item.to} end={item.end} onClick={onClose}
            className={({ isActive }) => `flex min-h-11 items-center gap-3 rounded-lg px-3 text-[0.9375rem] transition-colors ${isActive ? 'bg-sidebar-active font-medium text-white shadow-[inset_3px_0_0_var(--primary)]' : 'text-sidebar-foreground hover:bg-sidebar-active/60'}`}>
            <item.icon className="size-5 shrink-0" />{item.label}
          </NavLink>
        </li>)}</ul>
      </div>)}
    </nav>
    {footer && <div className="shrink-0 border-t border-white/10 p-3">{footer}</div>}
  </>
}

/** Fixed sidebar from `lg` up; the same content in a modal dialog below it. */
export function SidebarFrame({ mobileOpen, onMobileClose, dialogLabel, ...content }: { mobileOpen?: boolean; onMobileClose?: () => void; dialogLabel: string; brand: { icon: ComponentType<{ className?: string }>; title: string; caption: string }; label: string; groups: readonly NavGroup[]; footer?: ReactNode }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    if (mobileOpen && !dialog.current?.open) dialog.current?.showModal()
    if (!mobileOpen && dialog.current?.open) dialog.current.close()
  }, [mobileOpen])
  return <>
    <aside className="fixed inset-y-0 left-0 z-20 hidden w-68 flex-col bg-sidebar text-sidebar-foreground lg:flex"><SidebarContent {...content} /></aside>
    <dialog ref={dialog} aria-label={dialogLabel} onCancel={onMobileClose} onClose={onMobileClose} className="fixed inset-y-0 left-0 m-0 h-svh max-h-none w-72 max-w-[85vw] flex-col bg-sidebar p-0 text-sidebar-foreground backdrop:bg-black/60 open:flex"><SidebarContent {...content} onClose={onMobileClose} /></dialog>
  </>
}
