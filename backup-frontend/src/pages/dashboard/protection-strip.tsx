import { useRef, useState, type KeyboardEvent } from 'react'
import { Check, LoaderCircle, Minus, X } from 'lucide-react'
import { dayLabel, type StripDay } from '@/features/dashboard/protection'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'

const cells: Record<string, { icon?: typeof Check; className: string }> = {
  Succeeded: { icon: Check, className: 'border-success text-success' },
  Failed: { icon: X, className: 'border-error text-error' },
  Running: { icon: LoaderCircle, className: 'border-primary text-primary' },
  NoBackup: { icon: Minus, className: 'border-border text-text-muted' },
  NoJob: { className: 'border-dashed border-border' },
}

/** The 14 days of one job: one focus stop, arrow keys move between days, every day has its text equivalent. */
export function ProtectionStrip({ days, jobName }: { days: StripDay[]; jobName: string }) {
  const [active, setActive] = useState(days.length - 1)
  const items = useRef<(HTMLDivElement | null)[]>([])
  const move = (event: KeyboardEvent) => {
    const next = { ArrowRight: active + 1, ArrowLeft: active - 1, Home: 0, End: days.length - 1 }[event.key]
    if (next === undefined) return
    event.preventDefault()
    const index = Math.min(days.length - 1, Math.max(0, next))
    setActive(index)
    items.current[index]?.focus()
  }
  return <div role="list" aria-label={tx('Son 14 gün · {0}', { '0': jobName })} onKeyDown={move} className="flex gap-1">
    {days.map((day, index) => {
      const cell = cells[day.status] ?? cells.NoBackup
      const Icon = cell.icon
      return <div key={day.date} role="listitem" ref={element => { items.current[index] = element }} tabIndex={index === active ? 0 : -1} aria-label={dayLabel(day)} title={dayLabel(day)}
        onFocus={() => setActive(index)} className={cn('flex size-5 items-center justify-center rounded-sm border bg-surface', cell.className)}>
        {Icon && <Icon aria-hidden="true" className="size-3" />}
      </div>
    })}
  </div>
}
