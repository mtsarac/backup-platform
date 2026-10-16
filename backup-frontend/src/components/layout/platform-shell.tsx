import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Outlet } from 'react-router-dom'
import { Badge } from '@/components/ui/badge'
import { PlatformSidebar } from './platform-sidebar'
import { ShellBar } from './top-bar'

/** Layout route for /platform/*: never touches tenant context (no tenant selector, no tenant nav). */
export function PlatformShell() {
  useLanguage()
  const [mobileOpen, setMobileOpen] = useState(false)
  return <div className="flex min-h-svh bg-canvas">
    <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-surface focus:px-4 focus:py-2 focus:shadow-overlay">{tx("Ana içeriğe geç")}</a>
    <PlatformSidebar mobileOpen={mobileOpen} onMobileClose={() => setMobileOpen(false)} />
    <div className="flex min-w-0 flex-1 flex-col lg:ml-68">
      <ShellBar onMenuToggle={() => setMobileOpen((open) => !open)} context={<span className="text-sm font-medium text-text-heading">{tx("Platform yönetimi")}</span>} badges={<Badge tone="warning">{tx("PlatformAdmin")}</Badge>} />
      <main id="main" tabIndex={-1} className="mx-auto w-full min-w-0 max-w-[88rem] flex-1 space-y-6 p-4 outline-none sm:p-6 lg:px-8 lg:py-7"><Outlet /></main>
    </div>
  </div>
}
