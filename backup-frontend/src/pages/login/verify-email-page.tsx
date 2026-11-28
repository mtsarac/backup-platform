import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { AuthLayout } from '@/components/layout/auth-layout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { usePageTitle } from '@/hooks/use-page-title'
import { apiRequest, ApiError } from '@/lib/api/client'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'

export function VerifyEmailPage() {
  useLanguage()
  usePageTitle(tx('E-posta doğrulama'))
  const token = useSearchParams()[0].get('token') ?? ''
  const [done, setDone] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<ErrorDescription | null>(null)

  const confirm = async () => {
    setError(null)
    setLoading(true)
    try {
      await apiRequest('auth/email-verification/confirm', { method: 'POST', json: { token } })
      setDone(true)
    } catch (err) {
      setError(err instanceof ApiError && err.status === 400
        ? { what: tx('Doğrulama bağlantısı geçersiz veya süresi dolmuş.'), action: tx('Güvenlik ayarlarından yeni bir doğrulama e-postası isteyin.'), technical: [] }
        : describeError(err))
    } finally {
      setLoading(false)
    }
  }

  return (
    <AuthLayout title={tx('E-posta doğrulama')}>
          {done ? (
            <Notice tone="success">{tx('E-posta adresiniz doğrulandı.')}</Notice>
          ) : !token ? (
            <Notice tone="warning">{tx('Doğrulama bağlantısı geçersiz veya süresi dolmuş.')}</Notice>
          ) : (
            <Card padding="lg" className="space-y-4">
              <p className="text-sm text-text-muted">{tx('E-posta adresinizi doğrulamak için onaylayın.')}</p>
              {error && <ErrorNotice {...error} />}
              <Button className="w-full" disabled={loading} onClick={() => { void confirm() }}>{loading ? tx('Doğrulanıyor...') : tx('E-posta adresini doğrula')}</Button>
            </Card>
          )}
          <Link className="block text-sm text-primary underline-offset-4 hover:underline" to="/login">{tx('Girişe dön')}</Link>
    </AuthLayout>
  )
}
