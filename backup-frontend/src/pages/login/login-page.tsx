import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Key } from 'lucide-react'
import { BrandMark } from '@/components/ui/brand-mark'
import { Card } from '@/components/ui/card'
import { CheckField } from '@/components/ui/check-field'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { useAuth } from '@/features/auth/use-auth'
import { ROUTES } from '@/lib/routes'
import { apiRequest, ApiError } from '@/lib/api/client'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { usePageTitle } from '@/hooks/use-page-title'

export function LoginPage() {
  useLanguage()
  usePageTitle(tx('Giriş yapın'))
  const { login, completeSecondFactor, loginWithPasskey, verifySecondFactor, logout, isAuthenticated, sessionExpired } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [loginIdentifier, setLoginIdentifier] = useState('')
  const [password, setPassword] = useState('')
  const [rememberMe, setRememberMe] = useState(false)
  const [secondFactorPending, setSecondFactorPending] = useState(false)
  const [factorMode, setFactorMode] = useState<'totp' | 'recovery' | 'passkey'>('totp')
  const [totpCode, setTotpCode] = useState('')
  const [recoveryCode, setRecoveryCode] = useState('')
  const [error, setError] = useState<ErrorDescription | null>(null)
  const [loading, setLoading] = useState(false)

  const from = (location.state as { from?: { pathname: string } })?.from?.pathname ?? ROUTES.DASHBOARD

  if (isAuthenticated) {
    return <Navigate to={from} replace />
  }

  const handlePasswordSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      const result = await login(loginIdentifier, password, rememberMe)
      if (result && (result.secondFactorRequired || result.requiresSecondFactor)) {
        setSecondFactorPending(true)
        if (result.requiresSecondFactor && !result.secondFactorRequired) {
          setFactorMode('passkey')
        } else {
          setFactorMode('totp')
        }
        setPassword('')
        return
      }
      setPassword('')
      navigate(from, { replace: true })
    } catch (error) {
      setError(error instanceof ApiError && error.status === 401 ? { what: tx('Kullanıcı adı veya şifre hatalı.'), dataImpact: tx('Oturum açılmadı; verileriniz değişmedi.'), action: tx('Bilgilerinizi kontrol edip tekrar deneyin.'), technical: [] } : describeError(error))
    } finally {
      setLoading(false)
    }
  }

  const handleSecondFactorSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      if (factorMode === 'totp') {
        await apiRequest('auth/second-factor/totp', { method: 'POST', json: { code: totpCode } })
      } else {
        await apiRequest('auth/second-factor/recovery-code', { method: 'POST', json: { recoveryCode } })
      }
      await completeSecondFactor()
      navigate(from, { replace: true })
    } catch (error) {
      if (error instanceof ApiError && error.status === 429) {
        setError({ what: tx('Çok fazla başarısız deneme.'), dataImpact: tx('Giriş geçici olarak kilitlendi.'), action: tx('Lütfen daha sonra tekrar deneyin.'), technical: [] })
      } else if (error instanceof ApiError && (error.status === 401 || error.status === 400)) {
        setError({ what: factorMode === 'totp' ? tx('Doğrulama kodu hatalı veya süresi dolmuş.') : tx('Kurtarma kodu geçersiz veya daha önce kullanılmış.'), dataImpact: tx('Oturum açılmadı; verileriniz değişmedi.'), action: tx('Bilgilerinizi kontrol edip tekrar deneyin.'), technical: [] })
      } else {
        setError(describeError(error))
      }
    } finally {
      setLoading(false)
    }
  }

  const handlePasskeyLogin = async () => {
    setError(null)
    setLoading(true)
    try {
      await loginWithPasskey?.(rememberMe, loginIdentifier.trim() || undefined)
      navigate(from, { replace: true })
    } catch (error) {
      setError(error instanceof ApiError && error.status === 401 ? { what: tx('Geçiş anahtarı doğrulaması başarısız oldu.'), dataImpact: tx('Oturum açılmadı; verileriniz değişmedi.'), action: tx('Bilgilerinizi kontrol edip tekrar deneyin.'), technical: [] } : describeError(error))
    } finally {
      setLoading(false)
    }
  }

  const handleSecondFactor = async () => {
    setError(null)
    setLoading(true)
    try {
      await verifySecondFactor?.()
      setPassword('')
      navigate(from, { replace: true })
    } catch (error) {
      setError(error instanceof ApiError && error.status === 401 ? { what: tx('İkinci faktör doğrulaması başarısız oldu.'), dataImpact: tx('Oturum açılmadı; verileriniz değişmedi.'), action: tx('Bilgilerinizi kontrol edip tekrar deneyin.'), technical: [] } : describeError(error))
    } finally {
      setLoading(false)
    }
  }

  const cancelSecondFactor = async () => {
    setError(null)
    setLoading(true)
    try {
      await logout()
    } finally {
      setSecondFactorPending(false)
      setTotpCode('')
      setRecoveryCode('')
      setLoading(false)
    }
  }

  return (
    <div className="min-h-svh bg-canvas">
      <main className="flex min-h-svh items-center justify-center px-4 py-10">
        <div className="w-full max-w-sm space-y-6">
          <BrandMark size="sm" />
          <h1 className="text-2xl font-semibold text-text-heading">
            {secondFactorPending ? (factorMode === 'passkey' ? tx('İkinci faktör doğrulaması') : tx('İki faktörlü doğrulama')) : tx('Giriş yapın')}
          </h1>
          {secondFactorPending ? (
            factorMode === 'passkey' ? (
              <Card padding="lg" className="space-y-4">
                <Notice tone="info">{tx("Girişinizi tamamlamak için kayıtlı geçiş anahtarınızı kullanın.")}</Notice>
                {error && <ErrorNotice {...error} />}
                <Button type="button" className="w-full" disabled={loading} onClick={handleSecondFactor}>
                  {loading ? tx('Doğrulanıyor...') : tx('Geçiş anahtarı ile doğrula')}
                </Button>
                <div className="flex flex-col gap-2 text-center">
                  <button
                    type="button"
                    className="text-sm text-primary underline-offset-4 hover:underline"
                    onClick={() => { setError(null); setFactorMode('totp') }}
                  >
                    {tx('Doğrulama kodu kullan (TOTP)')}
                  </button>
                  <button
                    type="button"
                    className="text-sm text-primary underline-offset-4 hover:underline"
                    onClick={() => { setError(null); setFactorMode('recovery') }}
                  >
                    {tx('Kurtarma kodu kullan')}
                  </button>
                </div>
                <Button type="button" variant="outline" className="w-full" disabled={loading} onClick={cancelSecondFactor}>
                  {tx('Geri dön')}
                </Button>
              </Card>
            ) : factorMode === 'totp' ? (
              <Card as="form" padding="lg" onSubmit={handleSecondFactorSubmit} className="space-y-4">
                <Field label={tx('Doğrulama kodu')}>
                  {(control) => (
                    <TextInput
                      {...control}
                      autoFocus
                      autoComplete="one-time-code"
                      inputMode="numeric"
                      placeholder="000000"
                      value={totpCode}
                      onChange={(e) => setTotpCode(e.target.value)}
                      required
                    />
                  )}
                </Field>
                <Button type="submit" className="w-full" disabled={loading}>
                  {loading ? tx('Doğrulanıyor...') : tx('Doğrula')}
                </Button>
                <div className="flex flex-col gap-2 text-center">
                  <button
                    type="button"
                    className="text-sm text-primary underline-offset-4 hover:underline"
                    onClick={() => { setError(null); setFactorMode('recovery') }}
                  >
                    {tx('Kurtarma kodu kullan')}
                  </button>
                  <button
                    type="button"
                    className="text-sm text-primary underline-offset-4 hover:underline"
                    onClick={() => { setError(null); setFactorMode('passkey') }}
                  >
                    {tx('Geçiş anahtarı ile doğrula')}
                  </button>
                </div>
                {error && <ErrorNotice {...error} />}
                <Button type="button" variant="outline" className="w-full" disabled={loading} onClick={cancelSecondFactor}>
                  {tx('Girişe dön')}
                </Button>
              </Card>
            ) : (
              <Card as="form" padding="lg" onSubmit={handleSecondFactorSubmit} className="space-y-4">
                <Field label={tx('Kurtarma kodu')}>
                  {(control) => (
                    <TextInput
                      {...control}
                      autoFocus
                      placeholder="0000-0000-0000"
                      value={recoveryCode}
                      onChange={(e) => setRecoveryCode(e.target.value)}
                      required
                    />
                  )}
                </Field>
                <Button type="submit" className="w-full" disabled={loading}>
                  {loading ? tx('Doğrulanıyor...') : tx('Kurtarma koduyla giriş yap')}
                </Button>
                <div className="flex flex-col gap-2 text-center">
                  <button
                    type="button"
                    className="text-sm text-primary underline-offset-4 hover:underline"
                    onClick={() => { setError(null); setFactorMode('totp') }}
                  >
                    {tx('Doğrulama kodu kullan (TOTP)')}
                  </button>
                  <button
                    type="button"
                    className="text-sm text-primary underline-offset-4 hover:underline"
                    onClick={() => { setError(null); setFactorMode('passkey') }}
                  >
                    {tx('Geçiş anahtarı ile doğrula')}
                  </button>
                </div>
                {error && <ErrorNotice {...error} />}
                <Button type="button" variant="outline" className="w-full" disabled={loading} onClick={cancelSecondFactor}>
                  {tx('Girişe dön')}
                </Button>
              </Card>
            )
          ) : (
            <Card as="form" padding="lg" onSubmit={handlePasswordSubmit} className="space-y-4">
              <Field label={tx('Kullanıcı adı')} hint={tx('E-posta adresinizle de giriş yapabilirsiniz.')}>
                {(control) => <TextInput {...control} autoComplete="username" value={loginIdentifier} onChange={(e) => setLoginIdentifier(e.target.value)} required />}
              </Field>
              <Field label={tx('Şifre')}>
                {(control) => <TextInput {...control} type="password" value={password} autoComplete="current-password" onChange={(e) => setPassword(e.target.value)} required />}
              </Field>
              <CheckField label={tx('Beni hatırla')} checked={rememberMe} onChange={(event) => setRememberMe(event.target.checked)} />
              {sessionExpired && <Notice tone="warning">{tx('Oturumunuz sona erdi. Yeniden giriş yapın.')}</Notice>}
              {error && <ErrorNotice {...error} />}
              <Button type="submit" className="w-full" disabled={loading}>
                {loading ? tx('Giriş yapılıyor...') : tx('Giriş Yap')}
              </Button>
              <div className="relative my-2 text-center text-xs text-text-muted after:absolute after:inset-0 after:top-1/2 after:z-0 after:flex after:items-center after:border-t after:border-border">
                <span className="relative z-10 bg-surface px-2 text-text-muted">{tx('veya')}</span>
              </div>
              <Button type="button" variant="outline" className="w-full" disabled={loading} onClick={handlePasskeyLogin}>
                <Key aria-hidden="true" className="size-4" />
                {tx('Geçiş anahtarı ile giriş yap')}
              </Button>
            </Card>
          )}
          <LanguageSwitcher />
        </div>
      </main>
    </div>
  )
}
