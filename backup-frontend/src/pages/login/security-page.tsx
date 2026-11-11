import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { FormDialog } from '@/components/ui/form-dialog'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { PageHeader } from '@/components/ui/page-header'
import { Notice } from '@/components/ui/notice'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { usePageTitle } from '@/hooks/use-page-title'
import { apiRequest } from '@/lib/api/client'
import { tx, useLanguage } from '@/lib/i18n'

type SecurityStatus = {
  hasTotp: boolean
  remainingRecoveryCodes: number
}

type SetupData = {
  setupToken: string
  manualEntryKey: string
  qrUri: string
  qrSvg: string
}

export function SecurityPage() {
  useLanguage()
  usePageTitle(tx('Güvenlik ayarları'))
  const cache = useQueryClient()
  const security = useQuery({
    queryKey: ['auth', 'security'],
    queryFn: ({ signal }) => apiRequest<SecurityStatus>('auth/security', { signal }),
  })

  // Setup modal state
  const [setupOpen, setSetupOpen] = useState(false)
  const [setupData, setSetupData] = useState<SetupData | null>(null)
  const [setupPassword, setSetupPassword] = useState('')
  const [setupCode, setSetupCode] = useState('')
  const [setupPending, setSetupPending] = useState(false)
  const [setupError, setSetupError] = useState<unknown>(null)

  // Recovery codes display modal (shown once after enroll or regenerate)
  const [codesModalOpen, setCodesModalOpen] = useState(false)
  const [issuedCodes, setIssuedCodes] = useState<string[]>([])
  const [copied, setCopied] = useState(false)

  // Remove modal state
  const [removeOpen, setRemoveOpen] = useState(false)
  const [removePassword, setRemovePassword] = useState('')
  const [removeCode, setRemoveCode] = useState('')
  const [removePending, setRemovePending] = useState(false)
  const [removeError, setRemoveError] = useState<unknown>(null)

  // Regenerate modal state
  const [regenOpen, setRegenOpen] = useState(false)
  const [regenPassword, setRegenPassword] = useState('')
  const [regenCode, setRegenCode] = useState('')
  const [regenPending, setRegenPending] = useState(false)
  const [regenError, setRegenError] = useState<unknown>(null)

  async function startSetup() {
    setSetupError(null)
    setSetupPassword('')
    setSetupCode('')
    setSetupPending(true)
    try {
      const data = await apiRequest<SetupData>('auth/security/totp/setup', { method: 'POST' })
      setSetupData(data)
      setSetupOpen(true)
    } catch (err) {
      setSetupError(err)
    } finally {
      setSetupPending(false)
    }
  }

  async function submitEnroll() {
    if (!setupData) return
    setSetupPending(true)
    setSetupError(null)
    try {
      const res = await apiRequest<{ recoveryCodes: string[] }>('auth/security/totp/enroll', {
        method: 'POST',
        json: {
          setupToken: setupData.setupToken,
          code: setupCode,
          currentPassword: setupPassword,
        },
      })
      setSetupOpen(false)
      setSetupData(null)
      await cache.invalidateQueries({ queryKey: ['auth', 'security'] })
      setIssuedCodes(res.recoveryCodes)
      setCodesModalOpen(true)
    } catch (err) {
      setSetupError(err)
    } finally {
      setSetupPending(false)
    }
  }

  async function submitRemove() {
    setRemovePending(true)
    setRemoveError(null)
    try {
      await apiRequest<void>('auth/security/totp/remove', {
        method: 'POST',
        json: {
          currentPassword: removePassword || undefined,
          code: removeCode || undefined,
        },
      })
      setRemoveOpen(false)
      setRemovePassword('')
      setRemoveCode('')
      await cache.invalidateQueries({ queryKey: ['auth', 'security'] })
    } catch (err) {
      setRemoveError(err)
    } finally {
      setRemovePending(false)
    }
  }

  async function submitRegenerate() {
    setRegenPending(true)
    setRegenError(null)
    try {
      const res = await apiRequest<{ recoveryCodes: string[] }>('auth/security/recovery-codes/regenerate', {
        method: 'POST',
        json: {
          currentPassword: regenPassword || undefined,
          code: regenCode || undefined,
        },
      })
      setRegenOpen(false)
      setRegenPassword('')
      setRegenCode('')
      await cache.invalidateQueries({ queryKey: ['auth', 'security'] })
      setIssuedCodes(res.recoveryCodes)
      setCodesModalOpen(true)
    } catch (err) {
      setRegenError(err)
    } finally {
      setRegenPending(false)
    }
  }

  function copyRecoveryCodes() {
    void navigator.clipboard.writeText(issuedCodes.join('\n'))
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <main className="mx-auto max-w-3xl space-y-6 px-4 py-10">
      <div className="flex items-center justify-between gap-4">
        <Link className="text-sm text-primary underline-offset-4 hover:underline" to="/">
          {tx('Çalışma alanlarına dön')}
        </Link>
        <LanguageSwitcher />
      </div>

      <PageHeader
        title={tx('Güvenlik ayarları')}
        description={tx('İki faktörlü kimlik doğrulama ve kurtarma kodlarınızı yönetin.')}
      />

      <QueryState isPending={security.isPending} error={security.error} onRetry={() => { void security.refetch() }}>
        <div className="space-y-6">
          <Card padding="lg" className="space-y-4">
            <div className="flex flex-wrap items-center justify-between gap-4">
              <div>
                <h2 className="text-lg font-semibold text-text-heading">{tx('İki faktörlü doğrulama (TOTP)')}</h2>
                <p className="text-sm text-text-muted">
                  {security.data?.hasTotp
                    ? tx('Hesabınız bir kimlik doğrulama uygulamasıyla korunuyor.')
                    : tx('Hesabınızı bir kimlik doğrulama uygulaması (Google Authenticator vb.) ile koruyun.')}
                </p>
              </div>
              <div>
                {security.data?.hasTotp ? (
                  <Button variant="destructive" size="sm" onClick={() => { setRemoveError(null); setRemoveOpen(true) }}>
                    {tx('TOTP’yi Kaldır')}
                  </Button>
                ) : (
                  <Button size="sm" disabled={setupPending} onClick={() => { void startSetup() }}>
                    {tx('TOTP Etkinleştir')}
                  </Button>
                )}
              </div>
            </div>
          </Card>

          {security.data?.hasTotp && (
            <Card padding="lg" className="space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-4">
                <div>
                  <h2 className="text-lg font-semibold text-text-heading">{tx('Kurtarma kodları')}</h2>
                  <p className="text-sm text-text-muted">
                    {tx('Kalan kullanılabilir kurtarma kodu: {0}', { '0': security.data.remainingRecoveryCodes })}
                  </p>
                </div>
                <div>
                  <Button variant="outline" size="sm" onClick={() => { setRegenError(null); setRegenOpen(true) }}>
                    {tx('Kurtarma kodlarını yeniden oluştur')}
                  </Button>
                </div>
              </div>
            </Card>
          )}
        </div>
      </QueryState>

      {/* TOTP Setup Dialog */}
      {setupOpen && setupData && (
        <FormDialog
          title={tx('İki faktörlü doğrulama kurulumu')}
          submitLabel={tx('Doğrula ve Etkinleştir')}
          pendingLabel={tx('Etkinleştiriliyor...')}
          pending={setupPending}
          error={setupError}
          onSubmit={() => { void submitEnroll() }}
          onClose={() => setSetupOpen(false)}
        >
          <div className="space-y-4 text-sm">
            <p className="text-text-muted">
              {tx('Kimlik doğrulama uygulamanızla aşağıdaki QR kodunu tarayın veya gizli anahtarı manuel girin.')}
            </p>
            <div
              className="mx-auto flex size-48 items-center justify-center rounded-lg border border-border bg-white p-2 [&>svg]:size-full"
              dangerouslySetInnerHTML={{ __html: setupData.qrSvg }}
            />
            <div className="rounded-lg border border-border bg-surface-muted p-3 text-center">
              <span className="block text-xs font-medium text-text-muted">{tx('Gizli anahtar')}</span>
              <code className="select-all font-mono text-sm font-semibold tracking-wider text-text-heading">
                {setupData.manualEntryKey}
              </code>
            </div>
            <Field label={tx('Mevcut şifreniz')}>
              {(control) => (
                <TextInput
                  {...control}
                  type="password"
                  autoComplete="current-password"
                  value={setupPassword}
                  onChange={(e) => setSetupPassword(e.target.value)}
                  required
                />
              )}
            </Field>
            <Field label={tx('Doğrulama kodu')}>
              {(control) => (
                <TextInput
                  {...control}
                  inputMode="numeric"
                  placeholder="000000"
                  autoComplete="one-time-code"
                  value={setupCode}
                  onChange={(e) => setSetupCode(e.target.value)}
                  required
                />
              )}
            </Field>
          </div>
        </FormDialog>
      )}

      {/* Issued Recovery Codes Modal */}
      {codesModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="w-full max-w-md space-y-4 rounded-xl border border-border bg-surface p-6 shadow-overlay">
            <h2 className="text-lg font-semibold text-text-heading">{tx('Kurtarma kodlarınız')}</h2>
            <Notice tone="warning">
              {tx('Bu kodlar yalnızca bir kez gösterilir. Kimlik doğrulama cihazınıza erişiminizi kaybederseniz hesabınıza erişmek için bu kodları kullanabilirsiniz.')}
            </Notice>
            <div className="grid grid-cols-2 gap-2 rounded-lg border border-border bg-surface-muted p-4 font-mono text-sm">
              {issuedCodes.map((code) => (
                <span key={code} className="select-all text-text-heading">
                  {code}
                </span>
              ))}
            </div>
            <div className="flex flex-wrap justify-end gap-2 pt-2">
              <Button variant="outline" onClick={copyRecoveryCodes}>
                {copied ? tx('Kopyalandı!') : tx('Kodları kopyala')}
              </Button>
              <Button onClick={() => setCodesModalOpen(false)}>{tx('Tamam')}</Button>
            </div>
          </div>
        </div>
      )}

      {/* Remove TOTP Dialog */}
      {removeOpen && (
        <FormDialog
          title={tx('İki faktörlü doğrulamayı kaldır')}
          description={tx('Güvenlik doğrulaması için mevcut şifrenizi veya geçerli bir TOTP kodunu girin.')}
          submitLabel={tx('Kaldır')}
          pendingLabel={tx('Kaldırılıyor...')}
          pending={removePending}
          error={removeError}
          onSubmit={() => { void submitRemove() }}
          onClose={() => setRemoveOpen(false)}
        >
          <Field label={tx('Mevcut şifre')}>
            {(control) => (
              <TextInput
                {...control}
                type="password"
                autoComplete="current-password"
                value={removePassword}
                onChange={(e) => setRemovePassword(e.target.value)}
              />
            )}
          </Field>
          <div className="text-center text-xs text-text-muted">{tx('veya')}</div>
          <Field label={tx('Doğrulama kodu (TOTP)')}>
            {(control) => (
              <TextInput
                {...control}
                inputMode="numeric"
                placeholder="000000"
                value={removeCode}
                onChange={(e) => setRemoveCode(e.target.value)}
              />
            )}
          </Field>
        </FormDialog>
      )}

      {/* Regenerate Recovery Codes Dialog */}
      {regenOpen && (
        <FormDialog
          title={tx('Kurtarma kodlarını yeniden oluştur')}
          description={tx('Yeni kodlar oluşturulduğunda eski tüm kurtarma kodları geçersiz hale gelir. Güvenlik doğrulaması için şifrenizi veya TOTP kodunuzu girin.')}
          submitLabel={tx('Yeniden oluştur')}
          pendingLabel={tx('Oluşturuluyor...')}
          pending={regenPending}
          error={regenError}
          onSubmit={() => { void submitRegenerate() }}
          onClose={() => setRegenOpen(false)}
        >
          <Field label={tx('Mevcut şifre')}>
            {(control) => (
              <TextInput
                {...control}
                type="password"
                autoComplete="current-password"
                value={regenPassword}
                onChange={(e) => setRegenPassword(e.target.value)}
              />
            )}
          </Field>
          <div className="text-center text-xs text-text-muted">{tx('veya')}</div>
          <Field label={tx('Doğrulama kodu (TOTP)')}>
            {(control) => (
              <TextInput
                {...control}
                inputMode="numeric"
                placeholder="000000"
                value={regenCode}
                onChange={(e) => setRegenCode(e.target.value)}
              />
            )}
          </Field>
        </FormDialog>
      )}
    </main>
  )
}
