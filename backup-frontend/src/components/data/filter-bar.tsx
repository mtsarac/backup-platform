import { tx, useLanguage } from '@/lib/i18n'
import { Search } from 'lucide-react'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

interface FilterBarProps {
  search: string
  onSearchChange: (value: string) => void
  placeholder?: string
  children?: ReactNode
  className?: string
}

export function FilterBar({
  search,
  onSearchChange,
  placeholder = tx('Ara...'),
  children,
  className,
}: FilterBarProps) {
  useLanguage()
  return (
    <div
      className={cn(
        'flex flex-wrap items-center gap-3 rounded-lg border border-border bg-surface p-3',
        className
      )}
    >
      <div className="relative flex-1">
        <Search className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-text-muted" />
        <input
          type="search"
          value={search}
          onChange={(event) => onSearchChange(event.target.value)}
          placeholder={placeholder}
          className="h-8 w-full rounded-md border border-border bg-surface-2 pl-9 pr-3 text-sm outline-none focus:border-ring"
        />
      </div>
      {children}
    </div>
  )
}
