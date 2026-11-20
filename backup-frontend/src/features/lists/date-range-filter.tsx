import { useState } from 'react'
import { FilterField } from '@/components/ui/filters'
import { TextInput } from '@/components/ui/text-input'
import { tx, useLanguage } from '@/lib/i18n'
import { dayParam, isCompleteDay, isInvertedRange } from './url-params'

/** Keep partial dates local until complete or the field loses focus. */
function DayInput({ label, value, min, max, onCommit }: { label: string; value: string; min?: string; max?: string; onCommit: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  const [seen, setSeen] = useState(value)
  if (value !== seen) { setSeen(value); setDraft(value) }
  const commit = (next: string) => { if (next !== value) onCommit(next) }
  return <FilterField label={label}>
    <TextInput aria-label={label} type="date" min={min || undefined} max={max || undefined} value={draft}
      onChange={event => { const next = event.target.value; setDraft(next); if (isCompleteDay(next)) commit(next) }}
      onBlur={() => { if (!draft || dayParam(draft)) commit(draft); else setDraft(value) }} />
  </FilterField>
}

/** The from and to days of a list (`YYYY-MM-DD` filters), with the message beside them while the start is after the end. The page decides what an inverted range loads: nothing (see `isInvertedRange`). */
export function DateRangeFilter({ from, to, fromLabel, toLabel, onChange }: { from: string; to: string; fromLabel: string; toLabel: string; onChange: (which: 'from' | 'to', value: string) => void }) {
  useLanguage()
  return <>
    <DayInput label={fromLabel} value={from} max={to} onCommit={value => onChange('from', value)} />
    <DayInput label={toLabel} value={to} min={from} onCommit={value => onChange('to', value)} />
    {isInvertedRange(from, to) && <p role="alert" className="text-sm text-error sm:col-span-2 xl:col-span-4">{tx('Başlangıç tarihi bitiş tarihinden sonra olamaz.')}</p>}
  </>
}
