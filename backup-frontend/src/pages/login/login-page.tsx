import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { BrandMark } from '@/components/ui/brand-mark'
import { Card } from '@/components/ui/card'
import { CheckField } from '@/components/ui/check-field'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { useAuth } from '@/features/auth/use-auth'
import { ROUTES } from '@/lib/routes'
import { ApiError } from '@/lib/api/client'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { usePageTitle } from '@/hooks/use-page-title'

export function LoginPage() {
  useLanguage()
  usePageTitle(tx('Giriş yapın'))
  const { login, isAuthenticated, sessionExpired } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [loginIdentifier, setLoginIdentifier] = useState('')
  const [password, setPassword] = useState('')
  const [rememberMe, setRememberMe] = useState(false)
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
      await login(loginIdentifier, password, rememberMe)
      setPassword('')
      navigate(from, { replace: true })
    } catch (error) {
      setError(error instanceof ApiError && error.status === 401 ? { what: tx('Kullanıcı adı veya şifre hatalı.'), dataImpact: tx('Oturum açılmadı; verileriniz değişmedi.'), action: tx('Bilgilerinizi kontrol edip tekrar deneyin.'), technical: [] } : describeError(error))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="min-h-svh bg-canvas">
      <main className="flex min-h-svh items-center justify-center px-4 py-10">
        <div className="w-full max-w-sm space-y-6">
          <BrandMark size="sm" />
          <h1 className="text-2xl font-semibold text-text-heading">{tx("Giriş yapın")}</h1>
          <Card as="form" padding="lg" onSubmit={handleSubmit} className="space-y-4">
            <Field label={tx("Kullanıcı adı")}>{(control) => <TextInput {...control} autoComplete="username" value={loginIdentifier} onChange={(e) => setLoginIdentifier(e.target.value)} required />}</Field>
            <Field label={tx("Şifre")}>{(control) => <TextInput {...control} type="password" value={password} autoComplete="current-password" onChange={(e) => setPassword(e.target.value)} required />}</Field>
            <CheckField label={tx('Beni hatırla')} checked={rememberMe} onChange={event => setRememberMe(event.target.checked)} />
            {sessionExpired && <Notice tone="warning">{tx("Oturumunuz sona erdi. Yeniden giriş yapın.")}</Notice>}
            {error && <ErrorNotice {...error} />}
            <Button type="submit" className="w-full" disabled={loading}>{loading ? tx('Giriş yapılıyor...') : tx('Giriş Yap')}</Button>
          </Card>
          <LanguageSwitcher />
        </div>
      </main>
    </div>
  )
}
