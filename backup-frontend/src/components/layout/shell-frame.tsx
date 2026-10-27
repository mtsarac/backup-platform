import { tx, useLanguage } from '@/lib/i18n'
import { useState, type ComponentType, type ReactNode } from 'react'

export interface ShellSidebarProps { mobileOpen: boolean; onMobileClose: () => void }

/** Page skeleton shared by the tenant and platform shells: skip link, sidebar (drawer on phones), top bar and main landmark. Each shell supplies only its own sidebar, top bar and content. */
export function ShellFrame({ Sidebar, TopBar, children }: { Sidebar: ComponentType<ShellSidebarProps>; TopBar: ComponentType<{ onMenuToggle: () => void }>; children: ReactNode }) {
  useLanguage()
  const [mobileOpen, setMobileOpen] = useState(false)
  return <div className="flex min-h-svh bg-canvas">
    <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-surface focus:px-4 focus:py-2 focus:shadow-overlay">{tx("Ana içeriğe geç")}</a>
    <Sidebar mobileOpen={mobileOpen} onMobileClose={() => setMobileOpen(false)} />
    <div className="flex min-w-0 flex-1 flex-col lg:ml-68">
      <TopBar onMenuToggle={() => setMobileOpen((open) => !open)} />
      <main id="main" tabIndex={-1} className="mx-auto w-full min-w-0 max-w-[88rem] flex-1 space-y-6 p-4 outline-none sm:p-6 lg:px-8 lg:py-7">{children}</main>
    </div>
  </div>
}
