import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { errorMessage } from '@/lib/api/failure-messages'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import { FormDialog } from './form-dialog'
import { useAssignSubscriptionPlan, usePlans } from './hooks'
import { resolveOverride, type OverrideDraft } from './override-draft'
import { OverrideFields } from './override-fields'
import { formatGb } from './storage'
import type { AssignSubscriptionInput, Subscription } from './types'

/** The picker lists only what the backend returns for enabled plans; system plans stay hidden until asked for. */
export function AssignPlanDialog({ tenantId, current, onClose }: { tenantId: string; current: Subscription | null; onClose: () => void }) {
  useLanguage()
  const changing = current !== null
  const plans = usePlans({ enabled: 'true', page: 1, pageSize: 100 })
  const [planId, setPlanId] = useState('')
  const [showSystem, setShowSystem] = useState(false)
  const [override, setOverride] = useState<OverrideDraft>({ kind: 'none', gb: '' })
  const [overrideError, setOverrideError] = useState('')
  const [endsAt, setEndsAt] = useState('')
  const [endsAtError, setEndsAtError] = useState('')
  const assign = useAssignSubscriptionPlan(tenantId)
  const options = plans.data?.items.filter((plan) => showSystem || !plan.isSystem) ?? []
  function submit() {
    const input: AssignSubscriptionInput = { planId }
    const resolved = resolveOverride(override)
    if ('error' in resolved) { setOverrideError(resolved.error); return }
    setOverrideError('')
    if (resolved.value.kind !== 'none') input.storageLimitOverride = resolved.value
    if (endsAt) {
      const date = new Date(endsAt)
      if (Number.isNaN(date.getTime())) { setEndsAtError(tx('Geçerli bir bitiş tarihi girin.')); return }
      input.endsAt = date.toISOString()
    }
    setEndsAtError('')
    assign.mutate(input, { onSuccess: onClose })
  }
  return <FormDialog title={changing ? tx('Planı değiştir') : tx('Plan ata')} submitLabel={changing ? tx('Planı değiştir') : tx('Plan ata')} pendingLabel="Kaydediliyor…"
    description={changing ? tx("Mevcut abonelik “{0}” planından değiştirilir ve geçmişte “Değiştirildi” olarak kalır. Override yeni plana taşınmaz.", { "0": current.planName }) : tx('Bu tenant için yeni bir abonelik başlatılır.')}
    pending={assign.isPending} disabled={!planId} error={assign.error ? errorMessage(assign.error) : undefined} onSubmit={submit} onClose={onClose}>
    <Field label="Plan">{(control) => <select {...control} className={`${selectClass} w-full`} value={planId} disabled={plans.isPending} onChange={(event) => setPlanId(event.target.value)}>
        <option value="">{plans.isPending ? tx('Planlar yükleniyor…') : plans.error ? tx('Planlar alınamadı') : tx('Plan seçin')}</option>
        {options.map((plan) => <option key={plan.planId} value={plan.planId} disabled={current?.planId === plan.planId}>{plan.name} · {formatGb(plan.storageLimitBytes)}{current?.planId === plan.planId ? tx(' (mevcut)') : ''}</option>)}
      </select>}</Field>
    {plans.error && <p role="alert" className="text-sm text-error">{errorMessage(plans.error)}</p>}
    {plans.data && plans.data.totalCount > plans.data.items.length && <p className="text-xs text-text-muted">{tx("İlk")}{' '}{plans.data.items.length}{' '}{tx("plan gösteriliyor.")}</p>}
    <label className="flex min-h-6 items-center gap-2 text-sm text-text"><input type="checkbox" className="size-4" checked={showSystem} onChange={(event) => { setShowSystem(event.target.checked); setPlanId('') }} />{tx("Sistem planlarını göster")}</label>
    <OverrideFields draft={override} onChange={setOverride} error={overrideError} />
    <Field label={tx("Bitiş tarihi (isteğe bağlı)")} hint={tx("Boş bırakırsanız abonelik süresiz olur.")} error={endsAtError}>
      {(control) => <input {...control} type="datetime-local" className={inputClass} value={endsAt} onChange={(event) => setEndsAt(event.target.value)} />}
    </Field>
  </FormDialog>
}
