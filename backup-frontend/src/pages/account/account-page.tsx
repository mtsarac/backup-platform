import { useState } from 'react'
import { LogOut } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useAuth } from '@/features/auth/use-auth'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'

/** Profile facts come from the signed-in user the auth provider already holds; nothing else is fetched or invented. */
export function AccountPage() {
  useLanguage()
  const { user, logout } = useAuth()
  const [error, setError] = useState<ErrorDescription | null>(null)
  const [busy, setBusy] = useState(false)
  async function signOut() { setBusy(true); setError(null); try { await logout() } catch (error) { setError(describeError(error)) } finally { setBusy(false) } }
  return <div className="space-y-6">
    <PageHeader title={tx('Hesap')} description={tx('Oturum açtığınız hesabın bilgileri.')} />
    <Card padding="lg"><dl className="grid gap-4 sm:grid-cols-[10rem_1fr]">
      <dt className="text-sm text-text-muted">{tx('Görünen ad')}</dt><dd className="min-w-0 break-words font-medium text-text-heading">{user?.displayName}</dd>
      <dt className="text-sm text-text-muted">{tx('Rol')}</dt><dd><Badge tone={user?.role === 'PlatformAdmin' ? 'warning' : 'info'}>{user?.role === 'PlatformAdmin' ? tx('Platform Yöneticisi') : tx('UserAdmin')}</Badge></dd>
    </dl></Card>
    <div><Button variant="outline" disabled={busy} onClick={() => { void signOut() }}><LogOut aria-hidden="true" className="size-4" />{tx('Çıkış')}</Button></div>
    {error && <ErrorNotice {...error} />}
  </div>
}
