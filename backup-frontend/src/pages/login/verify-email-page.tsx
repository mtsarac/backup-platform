import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { BrandMark } from '@/components/ui/brand-mark'
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
        ? { what: tx('Doğrulama bağlantısı geçersiz veya süresi dolmuş.'), dataImpact: tx('E-posta adresiniz doğrulanmadı.'), action: tx('Güvenlik ayarlarından yeni bir doğrulama e-postası isteyin.'), technical: [] }
        : describeError(err))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="min-h-svh bg-canvas">
      <main className="flex min-h-svh items-center justify-center px-4 py-10">
        <div className="w-full max-w-sm space-y-6">
          <BrandMark size="sm" />
          <h1 className="text-2xl font-semibold text-text-heading">{tx('E-posta doğrulama')}</h1>
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
          <Link className="block text-sm text-primary underline-offset-4 hover:underline" to="/">{tx('Çalışma alanlarına dön')}</Link>
          <LanguageSwitcher />
        </div>
      </main>
    </div>
  )
}
