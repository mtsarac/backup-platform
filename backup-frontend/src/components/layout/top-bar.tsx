import { useState } from 'react'
import { Menu, Moon, Sun } from 'lucide-react'
import { A11yToolbar } from './a11y-toolbar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useTheme } from '@/hooks/use-theme'
import { errorMessage } from '@/lib/api/failure-messages'

export function TopBar({ onMenuToggle }: { onMenuToggle: () => void }) {
  const { logout, user, tenants } = useAuth()
  const { tenantId, tenant, switchTenant } = useTenant()
  const { resolvedTheme, toggleTheme } = useTheme()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  async function signOut() { setBusy(true); setError(''); try { await logout() } catch (error) { setError(errorMessage(error)) } finally { setBusy(false) } }
  return <>
    <header className="flex min-h-14 min-w-0 flex-wrap items-center justify-between gap-2 border-b border-border bg-surface px-4 py-2 sm:px-6">
      <div className="flex min-w-0 items-center gap-3">
        <Button variant="ghost" size="icon" className="lg:hidden" onClick={onMenuToggle} aria-label="Menüyü aç"><Menu /></Button>
        <label htmlFor="tenant-select" className="text-sm text-text-muted">Tenant</label>
        <select id="tenant-select" value={tenantId} onChange={event => { void switchTenant(event.target.value) }} className="max-w-48 rounded-md border border-border bg-surface-2 px-2 py-1 text-sm sm:max-w-72">{tenants.map(item => <option key={item.tenantId} value={item.tenantId}>{item.name}</option>)}</select>
      </div>
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <Badge tone="info">{tenant.membershipRole ?? tenant.role}</Badge>
        {user?.isPlatformAdmin && <Badge tone="warning">PlatformAdmin · platform rolü</Badge>}
        <span className="hidden text-sm text-text-muted sm:inline">{user?.displayName}</span>
        <A11yToolbar />
        <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="Tema değiştir">{resolvedTheme === 'light' ? <Moon /> : <Sun />}</Button>
        <Button variant="ghost" size="sm" disabled={busy} onClick={() => { void signOut() }}>Çıkış</Button>
      </div>
    </header>
    {error && <p role="alert" className="px-6 py-2 text-sm text-error">{error}</p>}
  </>
}
