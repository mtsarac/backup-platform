import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { ErrorNotice } from '@/components/ui/notice'
import { describePasskeyError, type ErrorDescription } from '@/lib/api/failure-messages'
import { PageHeader } from '@/components/ui/page-header'
import { usePageTitle } from '@/hooks/use-page-title'
import { apiRequest } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import { createCredential, getAssertion } from '@/features/auth/webauthn'
import { RecoveryCodesDialog } from '@/features/auth/recovery-codes-dialog'
import { Key, Plus, Trash2 } from 'lucide-react'
import { EmptyState } from '@/components/ui/empty-state'
import { actionName } from '@/lib/accessible-name'

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
  const hasPasskeys = (passkeys.data?.length ?? 0) > 0
  const verifyWithPasskey = usePasskeyVerification && hasPasskeys

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
    if (!addName.trim() || (!verifyWithPasskey && !addPassword)) return
    setAddPending(true)
    setAddError(null)
    try {
      // Verify first: a failed verification must not leave an orphaned credential on the authenticator.
      const verification = verifyWithPasskey ? await managementVerification() : { currentPassword: addPassword }
      const { options, challengeId } = await apiRequest<{
        options: Parameters<typeof createCredential>[0]
        challengeId: string
      }>('auth/passkeys/register/options', { method: 'POST' })

      const credentialResponse = await createCredential(options)

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
      setAddError(describePasskeyError(err))
    } finally {
      setAddPending(false)
    }
  }

  async function handleDelete(e: React.FormEvent) {
    e.preventDefault()
    if (!deleteTarget || (!verifyWithPasskey && !deletePassword)) return
    setDeletePending(true)
    setDeleteError(null)
    try {
      await apiRequest<void>(`auth/passkeys/${encodeURIComponent(deleteTarget.id)}`, {
        method: 'DELETE',
        json: verifyWithPasskey ? await managementVerification() : { currentPassword: deletePassword }
      })

      await cache.invalidateQueries({ queryKey: ['auth', 'passkeys'] })
      setDeleteTarget(null)
      setDeletePassword('')
    } catch (err) {
      setDeleteError(describePasskeyError(err))
    } finally {
      setDeletePending(false)
    }
  }

  return (
    <div className="space-y-6">
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

      {recoveryCodes.length > 0 && <RecoveryCodesDialog codes={recoveryCodes} onClose={() => setRecoveryCodes([])} />}

      {addOpen && (
        <Card as="form" padding="lg" onSubmit={handleAdd} className="space-y-4">
          <h2 className="text-lg font-semibold text-text-heading">{tx('Geçiş anahtarı ekle')}</h2>
          <Field label={tx('Geçiş anahtarı adı')} required>
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
          {!verifyWithPasskey && <Field label={tx('Mevcut şifreniz')} hint={tx('Güvenlik doğrulaması için mevcut şifrenizi girin.')} required>
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
          {hasPasskeys && <Button type="button" variant="outline" onClick={() => setUsePasskeyVerification(!usePasskeyVerification)}>
            {verifyWithPasskey ? tx('Şifreyle doğrula') : tx('Geçiş anahtarıyla doğrula')}
          </Button>}
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
            <Button type="submit" disabled={addPending || !addName.trim() || (!verifyWithPasskey && !addPassword)}>
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
          {!verifyWithPasskey && <Field label={tx('Mevcut şifreniz')} hint={tx('Güvenlik doğrulaması için mevcut şifrenizi girin.')} required>
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
          {hasPasskeys && <Button type="button" variant="outline" onClick={() => setUsePasskeyVerification(!usePasskeyVerification)}>
            {verifyWithPasskey ? tx('Şifreyle doğrula') : tx('Geçiş anahtarıyla doğrula')}
          </Button>}
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
            <Button type="submit" variant="destructive" disabled={deletePending || (!verifyWithPasskey && !deletePassword)}>
              {deletePending ? tx('İşleniyor…') : tx('Kaldır')}
            </Button>
          </div>
        </Card>
      )}

      <QueryState
        isPending={passkeys.isPending}
        error={passkeys.error}
        empty={passkeys.data?.length === 0}
        emptyState={<EmptyState icon={Key} message={tx("Henüz geçiş anahtarı eklenmedi.")} />}
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
                  aria-label={actionName(tx("Kaldır"), passkey.name)}
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
    </div>
  )
}
