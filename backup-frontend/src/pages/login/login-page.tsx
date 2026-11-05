import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { LockKeyhole, ShieldCheck, Workflow } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { BrandMark } from '@/components/ui/brand-mark'
import { Card } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { useAuth } from '@/features/auth/use-auth'
import { ROUTES } from '@/lib/routes'
import { ApiError } from '@/lib/api/client'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { usePageTitle } from '@/hooks/use-page-title'

const promises = [
  { icon: ShieldCheck, get text() { return tx("Her yedek doğrulanır ve checksum ile kayıt altına alınır.") } },
  { icon: Workflow, get text() { return tx("Zamanlama, yeniden deneme ve saklama süreleri tek yerden yönetilir.") } },
  { icon: LockKeyhole, get text() { return tx("Her müşterinin verisi kendi alanında, ayrı yetkilerle tutulur.") } },
]

export function LoginPage() {
  useLanguage()
  usePageTitle(tx('Giriş yapın'))
  const { login, isAuthenticated, sessionExpired } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [loginIdentifier, setLoginIdentifier] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<ErrorDescription | null>(null)
  const [loading, setLoading] = useState(false)

  const from = (location.state as { from?: { pathname: string } })?.from?.pathname ?? ROUTES.DASHBOARD

  if (isAuthenticated) {
    return <Navigate to={from} replace />
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      await login(loginIdentifier, password)
      setPassword('')
      navigate(from, { replace: true })
    } catch (error) {
      setError(error instanceof ApiError && error.status === 401 ? { what: tx('Kullanıcı adı veya şifre hatalı.'), dataImpact: tx('Oturum açılmadı; verileriniz değişmedi.'), action: tx('Bilgilerinizi kontrol edip tekrar deneyin.'), technical: [] } : describeError(error))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="grid min-h-svh bg-canvas lg:grid-cols-[minmax(0,5fr)_minmax(0,6fr)]">
      <aside className="hidden flex-col justify-between bg-sidebar p-12 text-sidebar-foreground lg:flex">
        <BrandMark inverse />
        <div className="max-w-md space-y-8">
          <h2 className="text-3xl font-semibold leading-tight text-white">{tx("Veritabanı yedekleriniz, tek bakışta güvende.")}</h2>
          <ul className="space-y-4">{promises.map(({ icon: Icon, text }) => <li key={text} className="flex gap-3"><Icon aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-primary" /><span className="text-body text-sidebar-foreground">{tx(text)}</span></li>)}</ul>
        </div>
        <p className="text-sm text-sidebar-muted">{tx("Yönetim konsolu")}</p>
      </aside>
      <main className="flex items-center justify-center px-4 py-10">
        <div className="w-full max-w-sm space-y-6">
          <div className="flex justify-end"><LanguageSwitcher /></div>
          <BrandMark size="sm" className="lg:hidden" />
          <div className="space-y-1"><h1 className="text-2xl font-semibold text-text-heading">{tx("Giriş yapın")}</h1><p className="text-body text-text-muted">{tx("Hesabınızla yönetim konsoluna devam edin.")}</p></div>
          <Card as="form" padding="lg" onSubmit={handleSubmit} className="space-y-4">
            <Field label={tx("Kullanıcı adı")}>{(control) => <TextInput {...control} autoComplete="username" value={loginIdentifier} onChange={(e) => setLoginIdentifier(e.target.value)} required />}</Field>
            <Field label={tx("Şifre")}>{(control) => <TextInput {...control} type="password" value={password} autoComplete="current-password" onChange={(e) => setPassword(e.target.value)} required />}</Field>
            {sessionExpired && <Notice tone="warning">{tx("Oturumunuz sona erdi. Yeniden giriş yapın.")}</Notice>}
            {error && <ErrorNotice {...error} />}
            <Button type="submit" size="lg" className="w-full" disabled={loading}>{loading ? tx('Giriş yapılıyor...') : tx('Giriş Yap')}</Button>
          </Card>
          <p className="text-sm text-text-muted">{tx("Oturum güvenli cookie ile yönetilir. Tenant erişimleriniz girişten sonra gösterilir.")}</p>
        </div>
      </main>
    </div>
  )
}
