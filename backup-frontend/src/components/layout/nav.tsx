import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, type ComponentType, type ReactNode } from 'react'
import { X } from 'lucide-react'
import { Link, matchPath, useLocation } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { isFeatureEnabled, type FeatureKey } from '@/features/feature-flags/feature-flags'

export interface NavItem { label: string; to: string; icon: ComponentType<{ className?: string }>; end?: boolean; feature?: FeatureKey; /** Extra route prefixes (full paths) that keep this item active, e.g. the sibling lists and detail pages it stands for. */ activeFor?: readonly string[] }

function isActive(item: NavItem, pathname: string) {
  return !!matchPath({ path: item.to, end: item.end ?? false }, pathname) || !!item.activeFor?.some((prefix) => !!matchPath({ path: prefix, end: false }, pathname))
}
export interface NavGroup { heading?: string; items: readonly NavItem[] }

function Brand({ icon: Icon, title, caption }: { icon: ComponentType<{ className?: string }>; title: string; caption: string }) {
  useLanguage()
  return <div className="flex items-center gap-3">
    <span className="flex size-7 items-center justify-center rounded-md bg-primary text-primary-foreground"><Icon aria-hidden="true" className="size-4" /></span>
    <span className="min-w-0 leading-tight"><span className="block font-semibold text-sidebar-foreground">{title}</span><span className="block text-xs text-sidebar-muted">{caption}</span></span>
  </div>
}

function SidebarContent({ brand, label, groups, footer, onClose }: { brand: { icon: ComponentType<{ className?: string }>; title: string; caption: string }; label: string; groups: readonly NavGroup[]; footer?: ReactNode; onClose?: () => void }) {
  useLanguage()
  const { pathname } = useLocation()
  return <>
    <div className="flex h-14 shrink-0 items-center justify-between px-5">
      <Brand {...brand} />
      {onClose && <Button variant="ghost" size="icon" onClick={onClose} aria-label={tx("Menüyü kapat")} ><X /></Button>}
    </div>
    <nav aria-label={label} className="flex-1 space-y-4 overflow-y-auto px-3 py-3">
      {groups.map((group, index) => <div key={group.heading ?? index} role={group.heading ? 'group' : undefined} aria-label={group.heading}>
        {group.heading && <p aria-hidden="true" className="mb-1 px-3 text-xs font-medium text-sidebar-muted">{group.heading}</p>}
        <ul className="space-y-0.5">{group.items.filter((item) => !item.feature || isFeatureEnabled(item.feature)).map((item) => <li key={item.to}>
          {(() => { const active = isActive(item, pathname); return <Link to={item.to} onClick={onClose} aria-current={active ? 'page' : undefined}
            className={`flex min-h-9 pointer-coarse:min-h-11 items-center gap-3 rounded-md px-3 text-body transition-colors ${active ? 'bg-sidebar-active font-semibold text-text-heading' : 'text-sidebar-foreground hover:bg-sidebar-active/60'}`}>
            <item.icon className="size-4 shrink-0 text-sidebar-muted" /><span className="min-w-0 break-words">{tx(item.label)}</span>
          </Link> })()}
        </li>)}</ul>
      </div>)}
    </nav>
    {footer && <div onClick={onClose} className="shrink-0 border-t border-border p-3">{footer}</div>}
  </>
}

/** Fixed sidebar from `lg` up; the same content in a modal dialog below it. */
export function SidebarFrame({ mobileOpen, onMobileClose, dialogLabel, ...content }: { mobileOpen?: boolean; onMobileClose?: () => void; dialogLabel: string; brand: { icon: ComponentType<{ className?: string }>; title: string; caption: string }; label: string; groups: readonly NavGroup[]; footer?: ReactNode }) {
  useLanguage()
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    if (mobileOpen && !dialog.current?.open) dialog.current?.showModal()
    if (!mobileOpen && dialog.current?.open) dialog.current.close()
  }, [mobileOpen])
  return <>
    <aside className="fixed inset-y-0 left-0 z-20 hidden w-68 flex-col border-r border-border bg-sidebar text-sidebar-foreground lg:flex"><SidebarContent {...content} /></aside>
    <dialog ref={dialog} aria-label={dialogLabel} onCancel={onMobileClose} onClose={onMobileClose} className="fixed inset-y-0 left-0 m-0 h-svh max-h-none w-72 max-w-[85vw] flex-col bg-sidebar p-0 text-sidebar-foreground backdrop:bg-black/60 open:flex"><SidebarContent {...content} onClose={onMobileClose} /></dialog>
  </>
}
