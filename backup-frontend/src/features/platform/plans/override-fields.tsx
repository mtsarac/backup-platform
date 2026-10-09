import { selectClass, inputClass } from '../styles'
import { isOverrideKind, type OverrideDraft } from './override-draft'
import { overrideKinds, type OverrideKind } from './types'

const kindLabels: Record<OverrideKind, string> = { none: 'Yok (plan limiti geçerli)', limited: 'Sınırlı (GB)', unlimited: 'Limitsiz' }

export function OverrideFields({ draft, onChange, error, legend = 'Depolama limiti override' }: { draft: OverrideDraft; onChange: (draft: OverrideDraft) => void; error?: string; legend?: string }) {
  return <div className="space-y-2">
    <label className="block text-sm text-text-muted">{legend}
      <select className={`${selectClass} w-full`} value={draft.kind} onChange={(event) => { if (isOverrideKind(event.target.value)) onChange({ ...draft, kind: event.target.value }) }}>
        {overrideKinds.map((kind) => <option key={kind} value={kind}>{kindLabels[kind]}</option>)}
      </select>
    </label>
    {draft.kind === 'limited' && <label className="block text-sm text-text-muted">Override boyutu (GB)
      <input className={inputClass} inputMode="decimal" required value={draft.gb} aria-invalid={Boolean(error)} onChange={(event) => onChange({ ...draft, gb: event.target.value })} />
      <span className="mt-1 block text-xs">1 GB = 1.000.000.000 bayt (ondalık). En fazla 3 ondalık basamak.</span>
    </label>}
    {error && <p className="text-xs text-error">{error}</p>}
  </div>
}
