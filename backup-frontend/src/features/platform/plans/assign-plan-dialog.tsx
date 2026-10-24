import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { inputClass } from '@/components/ui/form-styles'
import { FormDialog } from '@/components/ui/form-dialog'
import { ErrorNotice } from '@/components/ui/notice'
import { SearchablePicker } from '@/components/ui/searchable-picker'
import { useAssignSubscriptionPlan, usePlans } from './hooks'
import { resolveOverride, type OverrideDraft } from './override-draft'
import { OverrideFields } from './override-fields'
import { formatGb } from './storage'
import type { AssignSubscriptionInput, Plan, Subscription } from './types'

/**
 * `replacing` is the live subscription that this assignment turns into "Değiştirildi"; its plan is not offered again.
 * Pass `null` to assign to a workspace with no live subscription (none yet, or an expired one).
 */
export function AssignPlanDialog({ tenantId, replacing, onClose }: { tenantId: string; replacing: Subscription | null; onClose: () => void }) {
  useLanguage()
  const [search, setSearch] = useState('')
  const plans = usePlans({ enabled: 'true', ...(search ? { search } : {}), page: 1, pageSize: 100 })
  const [chosen, setChosen] = useState<Plan | null>(null)
  const [showSystem, setShowSystem] = useState(false)
  const [override, setOverride] = useState<OverrideDraft>({ kind: 'none', gb: '' })
  const [overrideError, setOverrideError] = useState('')
  const [endsAt, setEndsAt] = useState('')
  const [endsAtError, setEndsAtError] = useState('')
  const assign = useAssignSubscriptionPlan(tenantId)
  const options = plans.data?.items.filter((plan) => (showSystem || !plan.isSystem) && plan.planId !== replacing?.planId) ?? []
  function submit() {
    if (!chosen) return
    const input: AssignSubscriptionInput = { planId: chosen.planId }
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
  return <FormDialog title={replacing ? tx('Planı değiştir') : tx('Plan ata')} submitLabel={replacing ? tx('Planı değiştir') : tx('Plan ata')} pendingLabel={tx("Kaydediliyor…")}
    description={replacing ? tx("Mevcut abonelik “{0}” planından değiştirilir ve geçmişte “Değiştirildi” olarak kalır. Override yeni plana taşınmaz.", { "0": replacing.planName }) : tx('Bu çalışma alanı için yeni bir abonelik başlatılır.')}
    pending={assign.isPending} disabled={!chosen} error={assign.error} onSubmit={submit} onClose={onClose}>
    <SearchablePicker label={tx("Plan")} placeholder={tx("Plan seçin")} value={chosen?.planId ?? ''} selectedLabel={chosen?.name} onSearchChange={setSearch}
      onChange={(planId) => setChosen(options.find((plan) => plan.planId === planId) ?? null)}
      options={options.map((plan) => ({ value: plan.planId, label: plan.name, description: formatGb(plan.storageLimitBytes) }))} />
    {plans.error && <ErrorNotice title={tx("Planlar alınamadı")} error={plans.error} onRetry={() => void plans.refetch()} />}
    {plans.data && plans.data.totalCount > plans.data.items.length && <p className="text-xs text-text-muted">{tx("İlk {0} plan gösteriliyor; diğerleri için adı yazarak arayın.", { "0": plans.data.items.length })}</p>}
    <label className="flex min-h-11 items-center gap-2 text-sm text-text"><input type="checkbox" className="size-4" checked={showSystem} onChange={(event) => { setShowSystem(event.target.checked); setChosen(null) }} />{tx("Sistem planlarını göster")}</label>
    <OverrideFields draft={override} onChange={setOverride} error={overrideError} />
    <Field label={tx("Bitiş tarihi (isteğe bağlı)")} hint={tx("Boş bırakırsanız abonelik süresiz olur.")} error={endsAtError}>
      {(control) => <input {...control} type="datetime-local" className={inputClass} value={endsAt} onChange={(event) => setEndsAt(event.target.value)} />}
    </Field>
  </FormDialog>
}
