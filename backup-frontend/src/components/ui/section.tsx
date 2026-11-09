import { tx, useLanguage } from '@/lib/i18n'
import { useId, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** A titled part of a page: heading, then content, no box. `flush` frames the body for tables and lists that bring their own rows; `danger` frames the whole section. */
export function Section({ title, description, actions, children, flush, className, tone = 'default' }: {
  title?: ReactNode; description?: ReactNode; actions?: ReactNode; children?: ReactNode; flush?: boolean; className?: string; tone?: 'default' | 'danger'
}) {
  useLanguage()
  const id = useId()
  return <section aria-labelledby={title ? id : undefined} className={cn('min-w-0', tone === 'danger' && 'rounded-lg border border-error/40 p-4', className)}>
    {(title || actions) && <div className={cn('flex flex-wrap items-end justify-between gap-3', !flush && 'border-b border-border pb-2')}>
      <div className="min-w-0 space-y-0.5">
        {title && <h2 id={id} className="text-base font-semibold text-text-heading">{title}</h2>}
        {description && <p className="text-sm text-text-muted">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>}
    {children !== undefined && <div className={cn(flush ? 'overflow-hidden rounded-lg border border-border bg-surface **:data-[slot=data-table]:rounded-none **:data-[slot=data-table]:border-0' : '', (title || actions) && (flush ? 'mt-3' : 'pt-4'))}>{children}</div>}
  </section>
}

/** Label/value rows for read-only facts. Wraps instead of truncating. */
export function Facts({ children, columns = 2 }: { children: ReactNode; columns?: 1 | 2 | 3 }) {
  useLanguage()
  return <dl className={cn('grid gap-x-8 gap-y-4', columns === 2 && 'sm:grid-cols-2', columns === 3 && 'sm:grid-cols-2 lg:grid-cols-3')}>{children}</dl>
}

export function Fact({ label, hint, children }: { label: ReactNode; hint?: ReactNode; children: ReactNode }) {
  useLanguage()
  return <div className="min-w-0"><dt className="text-sm text-text-muted">{tx(label)}</dt><dd className="mt-0.5 break-words text-body text-text">{children}</dd>{hint && <p className="mt-0.5 text-sm text-text-muted">{hint}</p>}</div>
}
