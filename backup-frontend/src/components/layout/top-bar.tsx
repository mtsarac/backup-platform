import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Menu } from 'lucide-react'
import { A11yToolbar } from './a11y-toolbar'
import { LanguageSwitcher } from './language-switcher'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { Select } from '@/components/ui/text-input'
import { InAppNotifications } from '@/features/notifications/in-app'

/** Shared by the tenant and platform shells: context on the left, account and display controls on the right. */
export function ShellBar({ onMenuToggle, context, badges }: { onMenuToggle: () => void; context: ReactNode; badges?: ReactNode }) {
  const { user } = useAuth()
  useLanguage()
  return <>
    <header className="sticky top-0 z-10 flex min-h-14 min-w-0 flex-wrap items-center justify-between gap-x-4 gap-y-2 border-b border-border bg-canvas px-4 py-2 sm:px-6">
      <div className="flex min-w-0 items-center gap-3 max-sm:w-full">
        <Button variant="outline" size="icon" className="lg:hidden" onClick={onMenuToggle} aria-label={tx("Menüyü aç")}><Menu aria-hidden="true" /></Button>
        {context}
      </div>
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <span className="hidden items-center gap-2 sm:inline-flex">{badges}</span>
        <A11yToolbar />
        {user && <InAppNotifications key={user.userId} userId={user.userId} />}
        <LanguageSwitcher />
      </div>
    </header>
  </>
}

export function TopBar({ onMenuToggle }: { onMenuToggle: () => void }) {
  const { user, tenants } = useAuth()
  const { tenantId, switchTenant, tenant } = useTenant()
  useLanguage()
  const options = tenants.some(item => item.tenantId === tenantId) ? tenants : tenant ? [...tenants, tenant] : tenants
  return <ShellBar onMenuToggle={onMenuToggle}
    context={<div className="flex min-w-0 items-center gap-2">
      <label htmlFor="tenant-select" className="text-sm text-text-muted">{tx("Çalışma Alanı")}</label>
      <Select id="tenant-select" value={tenantId} onChange={(event) => { void switchTenant(event.target.value) }} className="min-w-0 max-w-52 flex-1 text-sm font-medium sm:max-w-72">{options.map((item) => <option key={item.tenantId} value={item.tenantId}>{item.name}</option>)}</Select>
    </div>}
    badges={<>
      {user?.role === 'PlatformAdmin' && <Link className="text-sm text-primary underline-offset-4 hover:underline" to="/platform">{tx('Platform Yönetimi')}</Link>}
      {user && <Badge tone={user.role === 'PlatformAdmin' ? "warning" : "info"}>{user.role === 'PlatformAdmin' ? tx("Platform Yöneticisi") : tx("UserAdmin")}</Badge>}
    </>} />
}
