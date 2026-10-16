import { tx, useLanguage } from '@/lib/i18n'
import { Link, Navigate } from 'react-router-dom'
import { useState } from 'react'
import { ArrowRight, LogOut, ShieldCheck } from 'lucide-react'
import { useAuth } from '@/features/auth/use-auth'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { errorMessage } from '@/lib/api/failure-messages'
import { LanguageSwitcher } from '@/components/layout/language-switcher'

export function TenantSelectPage() {
  useLanguage()
  const { user, tenants, logout } = useAuth()
  const [error, setError] = useState('')
  if (tenants.length === 1) return <Navigate to={`/tenants/${tenants[0].tenantId}`} replace />
  return <main className="mx-auto max-w-3xl space-y-6 px-4 py-10">
    <div className="flex justify-end"><LanguageSwitcher /></div>
    <PageHeader title={tx("Tenant seçin")} description={<>{user?.displayName} · {user?.isPlatformAdmin ? tx('PlatformAdmin yetkisi tenant üyeliğinden ayrıdır.') : tx('Yönetmek istediğiniz tenant ile devam edin.')}</>}
      actions={<Button variant="outline" onClick={() => { void logout().catch(error => setError(errorMessage(error))) }}><LogOut aria-hidden="true" className="size-4" />{tx("Çıkış")}</Button>} />
    {tenants.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState title={tx("Henüz bir tenant üyeliğiniz yok")}>{tx("Tenant yöneticinizle iletişime geçin.")}</EmptyState></div>
      : <ul className="grid gap-3 sm:grid-cols-2">{tenants.map(tenant => <li key={tenant.tenantId}>
        <Link className="group flex h-full min-w-0 items-center justify-between gap-3 rounded-xl border border-border bg-surface p-5 shadow-card transition-colors hover:border-primary focus-visible:outline-2 focus-visible:outline-ring" to={`/tenants/${tenant.tenantId}`}>
          <span className="min-w-0"><span className="block break-words font-semibold text-text-heading">{tenant.name}</span><span className="text-sm text-text-muted">{tx(tenant.membershipRole ?? tenant.role)}</span></span>
          <ArrowRight aria-hidden="true" className="size-5 shrink-0 text-text-muted transition-transform group-hover:translate-x-0.5 group-hover:text-primary" />
        </Link></li>)}</ul>}
    {user?.isPlatformAdmin && <Link className="inline-flex min-w-0 items-center gap-2 rounded-lg border border-border bg-surface px-4 py-3 font-medium text-primary shadow-card hover:border-primary focus-visible:outline-2 focus-visible:outline-ring" to="/platform"><ShieldCheck aria-hidden="true" className="size-5" />{tx("Platform Administration")}</Link>}
    {error && <Notice tone="error">{tx(error)}</Notice>}
  </main>
}
