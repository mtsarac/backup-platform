import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { useSetStorageLimitOverride } from './hooks'
import { resolveOverride, type OverrideDraft } from './override-draft'
import { OverrideFields } from './override-fields'
import { toGbInput } from './storage'
import type { Subscription } from './types'

export function OverrideDialog({ tenantId, tenantName, subscription, onClose }: { tenantId: string; tenantName: string; subscription: Subscription; onClose: () => void }) {
  useLanguage()
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
  return <ConfirmDialog open onClose={onClose} title={tx("Depolama limiti değiştirilsin mi?")} confirmLabel={tx("Kaydet")}
    consequence={{
      what: tx("“{0}” çalışma alanının depolama limiti hemen değişir. Özel limit plan limitinin yerine geçer; “Yok” seçilirse plan limiti yeniden geçerli olur.", { "0": tenantName }),
      who: tx("Bu çalışma alanının tüm kullanıcıları ve yedekleme işleri etkilenir; yeni limitin üzerinde kalırsa yeni yedekler engellenebilir."),
      reversible: tx("Evet. Özel limiti istediğiniz zaman yeniden değiştirebilir veya kaldırabilirsiniz; mevcut yedekler silinmez."),
    }}
    pending={save.isPending} error={save.error} onConfirm={submit}>
    <OverrideFields draft={draft} onChange={setDraft} error={validation} />
  </ConfirmDialog>
}
