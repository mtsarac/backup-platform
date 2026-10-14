import { Field } from '@/components/ui/field'
import { selectClass, inputClass } from '../styles'
import { isOverrideKind, type OverrideDraft } from './override-draft'
import { overrideKinds, type OverrideKind } from './types'

const kindLabels: Record<OverrideKind, string> = { none: 'Yok (plan limiti geçerli)', limited: 'Sınırlı (GB)', unlimited: 'Limitsiz' }

export function OverrideFields({ draft, onChange, error, legend = 'Depolama limiti override' }: { draft: OverrideDraft; onChange: (draft: OverrideDraft) => void; error?: string; legend?: string }) {
  return <div className="space-y-4">
    <Field label={legend}>{(control) => <select {...control} className={`${selectClass} w-full`} value={draft.kind} onChange={(event) => { if (isOverrideKind(event.target.value)) onChange({ ...draft, kind: event.target.value }) }}>
      {overrideKinds.map((kind) => <option key={kind} value={kind}>{kindLabels[kind]}</option>)}
    </select>}</Field>
    {draft.kind === 'limited' && <Field label="Override boyutu (GB)" hint="1 GB = 1.000.000.000 bayt (ondalık). En fazla 3 ondalık basamak." error={error}>
      {(control) => <input {...control} className={inputClass} inputMode="decimal" required value={draft.gb} onChange={(event) => onChange({ ...draft, gb: event.target.value })} />}
    </Field>}
  </div>
}
