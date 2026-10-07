import { useState } from 'react'
import { Menu, Moon, Sun } from 'lucide-react'
import { Outlet } from 'react-router-dom'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { useTheme } from '@/hooks/use-theme'
import { errorMessage } from '@/lib/api/failure-messages'
import { PlatformSidebar } from './platform-sidebar'

function PlatformTopBar({ onMenuToggle }: { onMenuToggle: () => void }) {
  const { logout, user } = useAuth()
  const { resolvedTheme, toggleTheme } = useTheme()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  async function signOut() { setBusy(true); setError(''); try { await logout() } catch (error) { setError(errorMessage(error)) } finally { setBusy(false) } }
  return <>
    <header className="flex min-h-14 min-w-0 flex-wrap items-center justify-between gap-2 border-b border-border bg-surface px-4 py-2 sm:px-6">
      <div className="flex min-w-0 items-center gap-3">
        <Button variant="ghost" size="icon" className="lg:hidden" onClick={onMenuToggle} aria-label="Menüyü aç"><Menu className="size-5" /></Button>
        <span className="text-sm font-medium text-text-heading">Platform yönetimi</span>
      </div>
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <Badge tone="warning">PlatformAdmin</Badge>
        <span className="hidden text-sm text-text-muted sm:inline">{user?.displayName}</span>
        <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="Tema değiştir">{resolvedTheme === 'light' ? <Moon className="size-4" /> : <Sun className="size-4" />}</Button>
        <Button variant="ghost" size="sm" disabled={busy} onClick={() => { void signOut() }}>Çıkış</Button>
      </div>
    </header>
    {error && <p role="alert" className="px-6 py-2 text-sm text-error">{error}</p>}
  </>
}

/** Layout route for /platform/*: never touches tenant context (no tenant selector, no tenant nav). */
export function PlatformShell() {
  const [mobileOpen, setMobileOpen] = useState(false)
  return <div className="flex min-h-svh bg-surface-2">
    <PlatformSidebar mobileOpen={mobileOpen} onMobileClose={() => setMobileOpen(false)} />
    <div className="flex min-w-0 flex-1 flex-col lg:ml-60">
      <PlatformTopBar onMenuToggle={() => setMobileOpen((open) => !open)} />
      <main className="min-w-0 flex-1 p-4 sm:p-6"><Outlet /></main>
    </div>
  </div>
}
