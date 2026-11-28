import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { AuthLayout } from '@/components/layout/auth-layout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { TextInput } from '@/components/ui/text-input'
import { usePageTitle } from '@/hooks/use-page-title'
import { apiRequest, ApiError } from '@/lib/api/client'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'

export function ResetPasswordPage() {
  useLanguage()
  usePageTitle(tx('Şifre sıfırlama'))
  const token = useSearchParams()[0].get('token') ?? ''
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [done, setDone] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<ErrorDescription | null>(null)

  const fail = (what: string, action: string) => setError({ what, action, technical: [] })

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setError(null)
    if (password !== confirmation) return fail(tx('Yeni şifreler eşleşmiyor.'), tx('Lütfen yeni şifreyi tekrar kontrol edin.'))
    setLoading(true)
    try {
      await apiRequest('auth/password-reset/complete', { method: 'POST', json: { token, newPassword: password } })
      setDone(true)
    } catch (err) {
      const code = err instanceof ApiError && err.status === 400 ? err.code : undefined
      if (code === 'invalid_token') fail(tx('Sıfırlama bağlantısı geçersiz veya süresi dolmuş.'), tx('Yeni bir sıfırlama bağlantısı isteyin.'))
      else if (code === 'password_common_or_breached') fail(tx('Bu şifre çok yaygın veya sızdırılmış listelerde yer alıyor. Farklı bir şifre seçin.'), tx('Daha güvenli ve tahmin edilmesi zor bir şifre belirleyin.'))
      else if (code === 'password_too_short') fail(tx('Şifre en az 12 karakter olmalıdır.'), tx('Daha uzun bir şifre girin.'))
      else setError(describeError(err))
    } finally {
      setLoading(false)
    }
  }

  return (
    <AuthLayout title={tx('Şifre sıfırlama')}>
          {done ? (
            <Notice tone="success">{tx('Şifreniz güncellendi. Tüm oturumlarınız sonlandırıldı; yeni şifrenizle giriş yapın.')}</Notice>
          ) : !token ? (
            <Notice tone="warning">{tx('Sıfırlama bağlantısı geçersiz veya süresi dolmuş.')}</Notice>
          ) : (
            <Card as="form" padding="lg" onSubmit={submit} className="space-y-4">
              <Field label={tx('Yeni şifre')} hint={tx('En az 12 karakter olmalıdır.')} required>
                {(control) => <TextInput {...control} type="password" autoComplete="new-password" value={password} onChange={(event) => setPassword(event.target.value)} required />}
              </Field>
              <Field label={tx('Yeni şifre (tekrar)')} required>
                {(control) => <TextInput {...control} type="password" autoComplete="new-password" value={confirmation} onChange={(event) => setConfirmation(event.target.value)} required />}
              </Field>
              {error && <ErrorNotice {...error} />}
              <Button type="submit" className="w-full" disabled={loading}>{loading ? tx('Kaydediliyor…') : tx('Şifreyi sıfırla')}</Button>
            </Card>
          )}
          <Link className="block text-sm text-primary underline-offset-4 hover:underline" to="/login">{tx('Girişe dön')}</Link>
    </AuthLayout>
  )
}
