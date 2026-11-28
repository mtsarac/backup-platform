import { useId, useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { DisclosureChevron, disclosureTriggerClass } from '@/components/ui/disclosure'
import { tx, useLanguage } from '@/lib/i18n'
import { CopyButton } from '@/components/ui/copy-button'

export interface TechnicalItem {
  label: string
  value: string
}

export interface TechnicalDetailsProps {
  items: readonly TechnicalItem[]
  defaultOpen?: boolean
  /** Richer technical content (tables, cards) shown below the items. */
  children?: ReactNode
}

/** One collapsed line; open, a single small monospace block of `Anahtar: değer` lines with one copy button for the whole block. */
export function TechnicalDetails({ items, defaultOpen = false, children }: TechnicalDetailsProps) {
  useLanguage()
  const [open, setOpen] = useState(defaultOpen)
  const panel = useId()
  if (items.length === 0 && !children) return null
  return <div className="text-sm">
    <button type="button" aria-expanded={open} aria-controls={panel} onClick={() => setOpen(value => !value)} className={cn(disclosureTriggerClass, open && 'text-text-heading')}>
      <DisclosureChevron className={open ? 'rotate-90' : undefined} />{tx('Teknik ayrıntılar')}
    </button>
    {open && <div id={panel} role="region" aria-label={tx('Teknik ayrıntılar')} className="mt-1 space-y-3">
      {items.length > 0 && <div className="flex items-start gap-2 rounded-md border border-border bg-surface-2 p-2">
        <div className="min-w-0 flex-1 space-y-0.5 font-mono text-xs text-text">
          {items.map(item => <p key={item.label} className="whitespace-pre-wrap break-all"><span className="text-text-muted">{item.label}:</span> {item.value}</p>)}
        </div>
        <CopyButton value={items.map(item => `${item.label}: ${item.value}`).join('\n')} label={tx('Kopyala')} />
      </div>}
      {children && <div className="space-y-4">{children}</div>}
    </div>}
  </div>
}
