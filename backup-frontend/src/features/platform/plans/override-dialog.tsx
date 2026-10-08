import { useState } from 'react'
import { errorMessage } from '@/lib/api/failure-messages'
import { FormDialog } from './form-dialog'
import { useSetStorageLimitOverride } from './hooks'
import { resolveOverride, type OverrideDraft } from './override-draft'
import { OverrideFields } from './override-fields'
import { toGbInput } from './storage'
import type { Subscription } from './types'

export function OverrideDialog({ tenantId, subscription, onClose }: { tenantId: string; subscription: Subscription; onClose: () => void }) {
  const current = subscription.storageLimitOverride
  const [draft, setDraft] = useState<OverrideDraft>({ kind: current.kind, gb: current.kind === 'limited' && current.bytes !== null ? toGbInput(current.bytes) : '' })
  const [validation, setValidation] = useState('')
  const save = useSetStorageLimitOverride(tenantId)
  function submit() {
    const resolved = resolveOverride(draft)
    if ('error' in resolved) { setValidation(resolved.error); return }
    setValidation('')
    save.mutate(resolved.value, { onSuccess: onClose })
  }
  return <FormDialog title="Depolama limiti override" submitLabel="Kaydet" pendingLabel="Kaydediliyor…" pending={save.isPending}
    description="Override, bu aboneliğin plan limitinin yerine geçer. “Yok” seçilirse plan limiti geçerli olur."
    error={save.error ? errorMessage(save.error) : undefined} onSubmit={submit} onClose={onClose}>
    <OverrideFields draft={draft} onChange={setDraft} error={validation} />
  </FormDialog>
}
