import { tx, useLanguage } from '@/lib/i18n'
import { useId, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** A titled work surface. `flush` drops the body padding for tables and lists that bring their own rows. */
export function Section({ title, description, actions, children, flush, className, tone = 'default' }: {
  title?: ReactNode; description?: ReactNode; actions?: ReactNode; children?: ReactNode; flush?: boolean; className?: string; tone?: 'default' | 'danger'
}) {
  useLanguage()
  const id = useId()
  return <section aria-labelledby={title ? id : undefined} className={cn('rounded-xl border bg-surface shadow-card', tone === 'danger' ? 'border-error/40' : 'border-border', className)}>
    {(title || actions) && <div className={cn('flex flex-wrap items-start justify-between gap-3 px-5 pt-4', flush && 'border-b border-border pb-4')}>
      <div className="min-w-0 space-y-0.5">
        {title && <h2 id={id} className="text-base font-semibold text-text-heading">{title}</h2>}
        {description && <p className="text-sm text-text-muted">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>}
    {children !== undefined && <div className={cn(flush ? '' : 'p-5', !flush && (title || actions) && 'pt-4')}>{children}</div>}
  </section>
}

/** Label/value rows for read-only facts. Wraps instead of truncating. */
export function Facts({ children, columns = 2 }: { children: ReactNode; columns?: 1 | 2 | 3 }) {
  useLanguage()
  return <dl className={cn('grid gap-x-8 gap-y-4', columns === 2 && 'sm:grid-cols-2', columns === 3 && 'sm:grid-cols-2 lg:grid-cols-3')}>{children}</dl>
}

export function Fact({ label, children }: { label: ReactNode; children: ReactNode }) {
  useLanguage()
  return <div className="min-w-0"><dt className="text-sm text-text-muted">{tx(label)}</dt><dd className="mt-0.5 break-words text-[0.9375rem] text-text">{children}</dd></div>
}
