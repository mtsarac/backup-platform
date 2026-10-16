import { tx, useLanguage } from '@/lib/i18n'
import { useState, type FormEvent } from 'react'
import { Search } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { filterControlClass } from '@/components/ui/filters'
import { cn } from '@/lib/utils'

/** Submit-driven search so the URL (and the API) change once per search, not per keystroke. */
export function SearchForm({ value, onSearch, label, placeholder, className }: { value: string; onSearch: (value: string) => void; label: string; placeholder?: string; className?: string }) {
  useLanguage()
  const [draft, setDraft] = useState(value)
  const [lastValue, setLastValue] = useState(value)
  if (value !== lastValue) { setLastValue(value); setDraft(value) }
  function submit(event: FormEvent) { event.preventDefault(); onSearch(draft.trim()) }
  return <form role="search" onSubmit={submit} className={cn('flex min-w-0 flex-1 items-end gap-2', className)}>
    <label className="block min-w-0 flex-1 space-y-1 text-sm font-medium text-text-heading">{tx(label)}<span className="relative block"><Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted" aria-hidden="true" /><input type="search" value={draft} onChange={(event) => setDraft(event.target.value)} placeholder={placeholder} className={cn(filterControlClass, 'pl-9 font-normal')} /></span></label>
    <Button type="submit" variant="outline" className="h-10">{tx("Ara")}</Button>
  </form>
}
