import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { ErrorNotice } from '@/components/ui/notice'
import { describeError, type ErrorDescription } from '@/lib/api/failure-messages'
import { PageHeader } from '@/components/ui/page-header'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { usePageTitle } from '@/hooks/use-page-title'
import { apiRequest } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import { createCredential, getAssertion } from '@/features/auth/webauthn'
import { Key, Plus, Trash2 } from 'lucide-react'

export type PasskeyItem = {
  id: string
  name: string
  createdAt: string
  lastUsedAt: string | null
}

export function PasskeysPage() {
  useLanguage()
  usePageTitle(tx('Geçiş anahtarları'))
  const cache = useQueryClient()
  const passkeys = useQuery({
    queryKey: ['auth', 'passkeys'],
    queryFn: ({ signal }) => apiRequest<PasskeyItem[]>('auth/passkeys', { signal })
  })

  const [addOpen, setAddOpen] = useState(false)
  const [addName, setAddName] = useState('')
  const [addPassword, setAddPassword] = useState('')
  const [addPending, setAddPending] = useState(false)
  const [addError, setAddError] = useState<ErrorDescription | null>(null)
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([])
  const [usePasskeyVerification, setUsePasskeyVerification] = useState(false)

  const [deleteTarget, setDeleteTarget] = useState<PasskeyItem | null>(null)
  const [deletePassword, setDeletePassword] = useState('')
  const [deletePending, setDeletePending] = useState(false)
  const [deleteError, setDeleteError] = useState<ErrorDescription | null>(null)

  async function managementVerification() {
    const { options, challengeId } = await apiRequest<{ options: Parameters<typeof getAssertion>[0]; challengeId: string }>(
      'auth/passkeys/second-factor/options', { method: 'POST' },
    )
    return { verificationChallengeId: challengeId, verificationResponse: await getAssertion(options) }
  }

  async function handleAdd(e: React.FormEvent) {
    e.preventDefault()
    if (!addName.trim() || (!usePasskeyVerification && !addPassword)) return
    setAddPending(true)
    setAddError(null)
    try {
      const { options, challengeId } = await apiRequest<{
        options: Parameters<typeof createCredential>[0]
        challengeId: string
      }>('auth/passkeys/register/options', { method: 'POST' })

      const credentialResponse = await createCredential(options)
      const verification = usePasskeyVerification ? await managementVerification() : { currentPassword: addPassword }

      const result = await apiRequest<{ recoveryCodes: string[] }>('auth/passkeys/register', {
        method: 'POST',
        json: {
          challengeId,
          name: addName.trim(),
          response: credentialResponse,
          ...verification
        }
      })

      setRecoveryCodes(result.recoveryCodes ?? [])
      await cache.invalidateQueries({ queryKey: ['auth', 'passkeys'] })
      setAddOpen(false)
      setAddName('')
      setAddPassword('')
    } catch (err) {
      setAddError(describeError(err))
    } finally {
      setAddPending(false)
    }
  }

  async function handleDelete(e: React.FormEvent) {
    e.preventDefault()
    if (!deleteTarget || (!usePasskeyVerification && !deletePassword)) return
    setDeletePending(true)
    setDeleteError(null)
    try {
      await apiRequest<void>(`auth/passkeys/${encodeURIComponent(deleteTarget.id)}`, {
        method: 'DELETE',
        json: usePasskeyVerification ? await managementVerification() : { currentPassword: deletePassword }
      })

      await cache.invalidateQueries({ queryKey: ['auth', 'passkeys'] })
      setDeleteTarget(null)
      setDeletePassword('')
    } catch (err) {
      setDeleteError(describeError(err))
    } finally {
      setDeletePending(false)
    }
  }

  return (
    <main className="mx-auto max-w-3xl space-y-6 px-4 py-10">
      <div className="flex items-center justify-between gap-4">
        <div className="flex items-center gap-4 text-sm">
          <Link className="text-primary underline-offset-4 hover:underline" to="/">
            {tx('Çalışma alanlarına dön')}
          </Link>
          <span className="text-text-muted">·</span>
          <Link className="text-text-muted underline-offset-4 hover:underline" to="/sessions">
            {tx('Oturumlarım')}
          </Link>
        </div>
        <LanguageSwitcher />
      </div>

      <PageHeader
        title={tx('Geçiş anahtarları')}
        description={tx('Geçiş anahtarlarınızı görüntüleyin ve yönetin.')}
        actions={
          <Button
            disabled={addOpen || addPending}
            onClick={() => {
              setAddOpen(true)
              setAddError(null)
              setAddName('')
              setAddPassword('')
            }}
          >
            <Plus aria-hidden="true" className="size-4" />
            {tx('Geçiş anahtarı ekle')}
          </Button>
        }
      />

      {recoveryCodes.length > 0 && (
        <Card padding="lg" className="space-y-4">
          <h2 className="text-lg font-semibold text-text-heading">{tx('Kurtarma kodları')}</h2>
          <p>{tx('Bu kodları güvenli bir yerde saklayın. Her kod yalnızca bir kez kullanılabilir.')}</p>
          <ul>{recoveryCodes.map(code => <li key={code}><code>{code}</code></li>)}</ul>
          <Button type="button" onClick={() => setRecoveryCodes([])}>{tx('Kapat')}</Button>
        </Card>
      )}

      {addOpen && (
        <Card as="form" padding="lg" onSubmit={handleAdd} className="space-y-4">
          <h2 className="text-lg font-semibold text-text-heading">{tx('Geçiş anahtarı ekle')}</h2>
          <Field label={tx('Geçiş anahtarı adı')}>
            {(control) => (
              <TextInput
                {...control}
                placeholder={tx('Örn. MacBook Touch ID, YubiKey')}
                value={addName}
                onChange={(e) => setAddName(e.target.value)}
                required
              />
            )}
          </Field>
          {!usePasskeyVerification && <Field label={tx('Mevcut şifreniz')} hint={tx('Güvenlik doğrulaması için mevcut şifrenizi girin.')}>
            {(control) => (
              <TextInput
                {...control}
                type="password"
                autoComplete="current-password"
                value={addPassword}
                onChange={(e) => setAddPassword(e.target.value)}
                required
              />
            )}
          </Field>}
          <Button type="button" variant="outline" onClick={() => setUsePasskeyVerification(!usePasskeyVerification)}>
            {usePasskeyVerification ? tx('Şifreyle doğrula') : tx('Geçiş anahtarıyla doğrula')}
          </Button>
          {addError && <ErrorNotice {...addError} />}
          <div className="flex justify-end gap-2">
            <Button
              type="button"
              variant="outline"
              disabled={addPending}
              onClick={() => {
                setAddOpen(false)
                setAddError(null)
              }}
            >
              {tx('Vazgeç')}
            </Button>
            <Button type="submit" disabled={addPending || !addName.trim() || (!usePasskeyVerification && !addPassword)}>
              {addPending ? tx('İşleniyor…') : tx('Kaydet')}
            </Button>
          </div>
        </Card>
      )}

      {deleteTarget && (
        <Card as="form" padding="lg" onSubmit={handleDelete} className="space-y-4">
          <h2 className="text-lg font-semibold text-text-heading">{tx('Geçiş anahtarı kaldır')}</h2>
          <p className="text-sm text-text-muted">
            {tx('Bu geçiş anahtarını kaldırmak istediğinizden emin misiniz?')} (<strong>{deleteTarget.name}</strong>)
          </p>
          {!usePasskeyVerification && <Field label={tx('Mevcut şifreniz')} hint={tx('Güvenlik doğrulaması için mevcut şifrenizi girin.')}>
            {(control) => (
              <TextInput
                {...control}
                type="password"
                autoComplete="current-password"
                value={deletePassword}
                onChange={(e) => setDeletePassword(e.target.value)}
                required
              />
            )}
          </Field>}
          <Button type="button" variant="outline" onClick={() => setUsePasskeyVerification(!usePasskeyVerification)}>
            {usePasskeyVerification ? tx('Şifreyle doğrula') : tx('Geçiş anahtarıyla doğrula')}
          </Button>
          {deleteError && <ErrorNotice {...deleteError} />}
          <div className="flex justify-end gap-2">
            <Button
              type="button"
              variant="outline"
              disabled={deletePending}
              onClick={() => {
                setDeleteTarget(null)
                setDeleteError(null)
                setDeletePassword('')
              }}
            >
              {tx('Vazgeç')}
            </Button>
            <Button type="submit" variant="destructive" disabled={deletePending || (!usePasskeyVerification && !deletePassword)}>
              {deletePending ? tx('İşleniyor…') : tx('Kaldır')}
            </Button>
          </div>
        </Card>
      )}

      <QueryState
        isPending={passkeys.isPending}
        error={passkeys.error}
        empty={passkeys.data?.length === 0}
        onRetry={() => {
          void passkeys.refetch()
        }}
      >
        <Card padding="none">
          <ul className="divide-y divide-border">
            {passkeys.data?.map((passkey) => (
              <li key={passkey.id} className="flex flex-wrap items-center justify-between gap-4 p-5">
                <div className="min-w-0 space-y-1">
                  <div className="flex items-center gap-2">
                    <Key aria-hidden="true" className="size-4 text-text-muted" />
                    <p className="break-words font-semibold text-text-heading">{passkey.name}</p>
                  </div>
                  <p className="text-sm text-text-muted">
                    {tx('Kayıt tarihi')}: {formatDateTime(passkey.createdAt)}
                  </p>
                  {passkey.lastUsedAt && (
                    <p className="text-sm text-text-muted">
                      {tx('Son kullanım')}: {formatDateTime(passkey.lastUsedAt)}
                    </p>
                  )}
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  aria-label={tx('{0} geçiş anahtarını kaldır', { '0': passkey.name })}
                  onClick={() => {
                    setDeleteTarget(passkey)
                    setDeleteError(null)
                    setDeletePassword('')
                  }}
                >
                  <Trash2 aria-hidden="true" className="size-4" />
                  {tx('Kaldır')}
                </Button>
              </li>
            ))}
          </ul>
        </Card>
      </QueryState>
    </main>
  )
}
