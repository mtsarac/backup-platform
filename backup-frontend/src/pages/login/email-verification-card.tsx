import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Notice } from '@/components/ui/notice'
import { apiRequest } from '@/lib/api/client'
import { tx, useLanguage } from '@/lib/i18n'

/** Optional email verification: only a verified address can receive a password reset link. */
export function EmailVerificationCard({ emailVerified }: { emailVerified: boolean }) {
  useLanguage()
  const cache = useQueryClient()
  const availability = useQuery({
    queryKey: ['auth', 'email-recovery'],
    queryFn: ({ signal }) => apiRequest<{ available: boolean }>('auth/email-recovery', { signal }),
  })
  const [pending, setPending] = useState(false)
  const [sent, setSent] = useState(false)
  const [failed, setFailed] = useState(false)

  const send = async () => {
    setPending(true)
    setFailed(false)
    try {
      await apiRequest('auth/email-verification/request', { method: 'POST' })
      setSent(true)
      void cache.invalidateQueries({ queryKey: ['auth', 'security'] })
    } catch {
      setFailed(true)
    } finally {
      setPending(false)
    }
  }

  return (
    <Card padding="lg" className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold text-text-heading">{tx('E-posta doğrulama')}</h2>
          <p className="text-sm text-text-muted">
            {emailVerified
              ? tx('E-posta adresiniz doğrulandı; şifre sıfırlama bağlantıları bu adrese gönderilebilir.')
              : tx('Doğrulama isteğe bağlıdır. Şifre sıfırlama bağlantısı yalnızca doğrulanmış adreslere gönderilir.')}
          </p>
        </div>
        {!emailVerified && availability.data?.available && (
          <Button variant="outline" size="sm" disabled={pending} onClick={() => { void send() }}>{tx('Doğrulama e-postası gönder')}</Button>
        )}
      </div>
      {!emailVerified && availability.data && !availability.data.available && <Notice tone="warning">{tx('E-posta ile kurtarma bu platformda kullanılamıyor.')}</Notice>}
      {sent && <Notice tone="success">{tx('Doğrulama e-postası gönderildi. Bağlantı 24 saat geçerlidir.')}</Notice>}
      {failed && <Notice tone="warning">{tx('İşlem başarısız oldu.')}</Notice>}
    </Card>
  )
}
