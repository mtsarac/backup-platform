import { useState } from 'react'
import { Loader2 } from 'lucide-react'
import { tx } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { DatabaseLoginFields } from '@/features/backup-jobs/database-login-fields'
import type { DatabaseLogin } from '@/features/backup-jobs/types'
import { storeDatabaseLogin } from './api'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'

interface Props {
  tenantId: string
  agentId: string
  idPrefix: string
  /** The reference of the login already stored for this target, if any. */
  reference: string | null | undefined
  onStored: (reference: string) => void
}

/**
 * The database login for a restore target. It is encrypted on the server first and the form keeps only the returned reference, so the password never
 * sits in the restore request (which is retried and fingerprinted) and a retried submit stays idempotent.
 */
export function StoredLoginPanel({ tenantId, agentId, idPrefix, reference, onStored }: Props) {
  const [login, setLogin] = useState<DatabaseLogin | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<ErrorDescription | null>(null)
  const save = async () => {
    if (!login || !agentId) return
    setSaving(true)
    setError(null)
    try {
      const result = await storeDatabaseLogin(tenantId, agentId, login)
      onStored(result.reference)
      setLogin(null)
    } catch (failure) {
      setError(describeError(failure))
    } finally {
      setSaving(false)
    }
  }
  return <div className="space-y-3 sm:col-span-2">
    <div className="grid gap-3 sm:grid-cols-2"><DatabaseLoginFields idPrefix={idPrefix} login={login} hasStored={Boolean(reference)} onChange={setLogin} /></div>
    <Button type="button" variant="outline" disabled={!login?.userName.trim() || !login.password || !agentId || saving} onClick={save}>
      {saving && <Loader2 aria-hidden="true" className="animate-spin" />}{tx("Girişi kaydet")}
    </Button>
    {reference && !login && <Notice tone="success">{tx("Bu hedef için kayıtlı bir veritabanı girişi seçili.")}</Notice>}
    {error && <ErrorNotice {...error} />}
  </div>
}
