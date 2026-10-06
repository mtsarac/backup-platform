import { useState, type ReactNode } from 'react'
import { Sidebar } from './sidebar'
import { TopBar } from './top-bar'

export function AppShell({ children }: { children: ReactNode }) {
  const [mobileOpen, setMobileOpen] = useState(false)

  return (
    <div className="flex min-h-svh bg-surface-2">
      <Sidebar mobileOpen={mobileOpen} onMobileClose={() => setMobileOpen(false)} />
      <div className="flex min-w-0 flex-1 flex-col lg:ml-60">
        <TopBar onMenuToggle={() => setMobileOpen((open) => !open)} />
        <main className="min-w-0 flex-1 p-4 sm:p-6">{children}</main>
      </div>
    </div>
  )
}
