import { tx, useLanguage } from '@/lib/i18n'
import { Field } from '@/components/ui/field'
import { selectClass, inputClass } from '@/components/ui/form-styles'
import { isOverrideKind, type OverrideDraft } from './override-draft'
import { overrideKinds, type OverrideKind } from './types'

const kindLabels: Record<OverrideKind, string> = { get none() { return tx("Yok (plan limiti geçerli)") }, get limited() { return tx("Sınırlı (GB)") }, get unlimited() { return tx("Limitsiz") } }

export function OverrideFields({ draft, onChange, error, legend = tx('Depolama limiti override') }: { draft: OverrideDraft; onChange: (draft: OverrideDraft) => void; error?: string; legend?: string }) {
  useLanguage()
  return <div className="space-y-4">
    <Field label={legend}>{(control) => <select {...control} className={`${selectClass} w-full`} value={draft.kind} onChange={(event) => { if (isOverrideKind(event.target.value)) onChange({ ...draft, kind: event.target.value }) }}>
      {overrideKinds.map((kind) => <option key={kind} value={kind}>{kindLabels[kind]}</option>)}
    </select>}</Field>
    {draft.kind === 'limited' && <Field label={tx("Override boyutu (GB)")} hint={tx("1 GB = 1.000.000.000 bayt (ondalık). En fazla 3 ondalık basamak.")} error={error}>
      {(control) => <input {...control} className={inputClass} inputMode="decimal" required value={draft.gb} onChange={(event) => onChange({ ...draft, gb: event.target.value })} />}
    </Field>}
  </div>
}
