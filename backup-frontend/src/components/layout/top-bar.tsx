import { tx, useLanguage } from '@/lib/i18n'
import { useState, type ReactNode } from 'react'
import { LogOut, Menu, Moon, Sun } from 'lucide-react'
import { A11yToolbar } from './a11y-toolbar'
import { LanguageSwitcher } from './language-switcher'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useTheme } from '@/hooks/use-theme'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'

/** Shared by the tenant and platform shells: context on the left, account and display controls on the right. */
export function ShellBar({ onMenuToggle, context, badges }: { onMenuToggle: () => void; context: ReactNode; badges?: ReactNode }) {
  const { logout, user } = useAuth()
  const { resolvedTheme, toggleTheme } = useTheme()
  useLanguage()
  const [error, setError] = useState<ErrorDescription | null>(null)
  const [busy, setBusy] = useState(false)
  async function signOut() { setBusy(true); setError(null); try { await logout() } catch (error) { setError(describeError(error)) } finally { setBusy(false) } }
  return <>
    <header className="sticky top-0 z-10 flex min-h-16 min-w-0 flex-wrap items-center justify-between gap-x-4 gap-y-2 border-b border-border bg-surface/95 px-4 py-2 backdrop-blur sm:px-6">
      <div className="flex min-w-0 items-center gap-3 max-sm:w-full">
        <Button variant="outline" size="icon" className="lg:hidden" onClick={onMenuToggle} aria-label={tx("Menüyü aç")}><Menu /></Button>
        {context}
      </div>
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <span className="hidden items-center gap-2 sm:inline-flex">{badges}</span>
        <A11yToolbar />
        <LanguageSwitcher />
        <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label={tx("Temayı değiştir")}>{resolvedTheme === 'light' ? <Moon /> : <Sun />}</Button>
        <span className="hidden max-w-40 truncate text-sm text-text-muted xl:inline" title={user?.displayName}>{user?.displayName}</span>
        <Button variant="outline" size="sm" disabled={busy} onClick={() => { void signOut() }}><LogOut aria-hidden="true" className="size-4" />{tx("Çıkış")}</Button>
      </div>
    </header>
    {error && <div className="px-4 pt-3 sm:px-6"><ErrorNotice {...error} /></div>}
  </>
}

export function TopBar({ onMenuToggle }: { onMenuToggle: () => void }) {
  const { user, tenants } = useAuth()
  const { tenantId, tenant, switchTenant } = useTenant()
  useLanguage()
  return <ShellBar onMenuToggle={onMenuToggle}
    context={<div className="flex min-w-0 items-center gap-2">
      <label htmlFor="tenant-select" className="text-sm text-text-muted">{tx("Çalışma Alanı")}</label>
      <select id="tenant-select" value={tenantId} onChange={(event) => { void switchTenant(event.target.value) }} className="min-h-10 max-w-52 rounded-lg border border-border-strong/60 bg-surface px-3 text-sm font-medium text-text sm:max-w-72">{tenants.map((item) => <option key={item.tenantId} value={item.tenantId}>{item.name}</option>)}</select>
    </div>}
    badges={<><Badge tone="info">{tx(tenant.membershipRole ?? tenant.role)}</Badge>{user?.isPlatformAdmin && <Badge tone="warning">{tx("Platform Yöneticisi")}</Badge>}</>} />
}
