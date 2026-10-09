import { Link, Navigate } from 'react-router-dom'
import { useState } from 'react'
import { useAuth } from '@/features/auth/use-auth'
import { Button } from '@/components/ui/button'
import { errorMessage } from '@/lib/api/failure-messages'

export function TenantSelectPage() {
  const { user, tenants, logout } = useAuth()
  const [error, setError] = useState('')
  if (tenants.length === 1) return <Navigate to={`/tenants/${tenants[0].tenantId}`} replace />
  return <main className="mx-auto max-w-2xl space-y-6 p-6">
    <h1 className="text-2xl font-semibold text-text-heading">Tenant seçin</h1>
    <p className="text-text-muted">{user?.displayName} · {user?.isPlatformAdmin ? 'PlatformAdmin yetkisi tenant üyeliğinden ayrıdır.' : 'Yönetmek istediğiniz tenant ile devam edin.'}</p>
    {tenants.length === 0 ? <p>Henüz bir tenant üyeliğiniz yok. Tenant yöneticinizle iletişime geçin.</p> : <div className="grid gap-3 sm:grid-cols-2">{tenants.map(tenant => <Link className="min-w-0 rounded-lg border border-border bg-surface p-5 hover:border-primary focus-visible:outline-2 focus-visible:outline-ring" to={`/tenants/${tenant.tenantId}`} key={tenant.tenantId}><h2 className="break-words font-semibold text-text-heading">{tenant.name}</h2><span className="text-sm text-text-muted">{tenant.membershipRole ?? tenant.role}</span></Link>)}</div>}
    {user?.isPlatformAdmin && <Link className="inline-flex min-w-0 items-center gap-2 rounded-lg border border-border bg-surface px-4 py-3 font-medium text-primary hover:border-primary focus-visible:outline-2 focus-visible:outline-ring" to="/platform">Platform Administration</Link>}
    {error && <p role="alert" className="text-error">{error}</p>}
    <Button variant="outline" onClick={() => { void logout().catch(error => setError(errorMessage(error))) }}>Çıkış</Button>
  </main>
}
