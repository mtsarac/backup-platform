import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { BrandMark } from '@/components/ui/brand-mark'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { TextInput } from '@/components/ui/text-input'
import { usePageTitle } from '@/hooks/use-page-title'
import { apiRequest, ApiError } from '@/lib/api/client'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'

export function ForgotPasswordPage() {
  useLanguage()
  usePageTitle(tx('Şifremi unuttum'))
  const availability = useQuery({
    queryKey: ['auth', 'email-recovery'],
    queryFn: ({ signal }) => apiRequest<{ available: boolean }>('auth/email-recovery', { signal }),
  })
  const [identifier, setIdentifier] = useState('')
  const [sent, setSent] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<ErrorDescription | null>(null)

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setError(null)
    setLoading(true)
    try {
      await apiRequest('auth/password-reset/request', { method: 'POST', json: { loginIdentifier: identifier } })
      setSent(true)
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429
        ? { what: tx('Çok fazla istek gönderildi.'), dataImpact: tx('Şifreniz değişmedi.'), action: tx('Bir süre sonra tekrar deneyin.'), technical: [] }
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
          <h1 className="text-2xl font-semibold text-text-heading">{tx('Şifremi unuttum')}</h1>
          <QueryState isPending={availability.isPending} error={availability.error} onRetry={() => { void availability.refetch() }}>
            {availability.data?.available ? (
              sent ? (
                <Notice tone="success">{tx('Bu bilgiler doğrulanmış bir e-posta adresiyle eşleşiyorsa şifre sıfırlama bağlantısı gönderildi.')}</Notice>
              ) : (
                <Card as="form" padding="lg" onSubmit={submit} className="space-y-4">
                  <p className="text-sm text-text-muted">{tx('Şifre sıfırlama bağlantısı yalnızca doğrulanmış e-posta adreslerine gönderilir.')}</p>
                  <Field label={tx('Kullanıcı adı')} hint={tx('E-posta adresinizle de giriş yapabilirsiniz.')}>
                    {(control) => <TextInput {...control} autoComplete="username" value={identifier} onChange={(event) => setIdentifier(event.target.value)} required />}
                  </Field>
                  {error && <ErrorNotice {...error} />}
                  <Button type="submit" className="w-full" disabled={loading}>{loading ? tx('Gönderiliyor...') : tx('Sıfırlama bağlantısı gönder')}</Button>
                </Card>
              )
            ) : (
              <Notice tone="warning">{tx('E-posta ile kurtarma bu platformda kullanılamıyor. Bir Platform Yöneticisinden geçici şifre isteyin.')}</Notice>
            )}
          </QueryState>
          <Link className="block text-sm text-primary underline-offset-4 hover:underline" to="/login">{tx('Girişe dön')}</Link>
          <LanguageSwitcher />
        </div>
      </main>
    </div>
  )
}
