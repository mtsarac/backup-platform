import { useState, type FormEvent } from 'react'
import { Search } from 'lucide-react'
import { Button } from '@/components/ui/button'

/** Submit-driven search so the URL (and the API) change once per search, not per keystroke. */
export function SearchForm({ value, onSearch, label, placeholder }: { value: string; onSearch: (value: string) => void; label: string; placeholder?: string }) {
  const [draft, setDraft] = useState(value)
  const [lastValue, setLastValue] = useState(value)
  if (value !== lastValue) { setLastValue(value); setDraft(value) }
  function submit(event: FormEvent) { event.preventDefault(); onSearch(draft.trim()) }
  return <form role="search" onSubmit={submit} className="flex min-w-0 flex-1 items-end gap-2">
    <label className="min-w-0 flex-1 text-sm text-text-muted">{label}<span className="relative mt-1 block"><Search className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-text-muted" aria-hidden="true" /><input type="search" value={draft} onChange={(event) => setDraft(event.target.value)} placeholder={placeholder} className="block w-full rounded-lg border border-border bg-surface py-2 pl-9 pr-3 text-text" /></span></label>
    <Button type="submit" variant="outline" className="h-9">Ara</Button>
  </form>
}
