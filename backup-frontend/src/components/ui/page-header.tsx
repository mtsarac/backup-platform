import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { ChevronRight } from 'lucide-react'
import { Link } from 'react-router-dom'
import { usePageTitle } from '@/hooks/use-page-title'

export interface Crumb { label: string; to?: string }

/** The one page heading: breadcrumb trail, title, one-line purpose, and the page's primary actions. */
export function PageHeader({ title, description, crumbs, actions, meta }: {
  title: ReactNode; description?: ReactNode; crumbs?: readonly Crumb[]; actions?: ReactNode; meta?: ReactNode
}) {
  useLanguage()
  usePageTitle(typeof title === 'string' ? title : undefined)
  return <header className="space-y-2 pb-1">
    {crumbs && crumbs.length > 0 && <nav aria-label={tx("Sayfa yolu")}><ol className="flex flex-wrap items-center gap-1 text-sm text-text-muted">
      {crumbs.map((crumb, index) => <li key={`${crumb.label}-${index}`} className="inline-flex min-w-0 max-w-full items-center gap-1">
        {index > 0 && <ChevronRight aria-hidden="true" className="size-3.5 shrink-0" />}
        {crumb.to ? <Link to={crumb.to} className="min-w-0 break-words rounded-sm underline-offset-4 hover:text-text hover:underline">{tx(crumb.label)}</Link> : <span className="min-w-0 break-words" aria-current="page">{tx(crumb.label)}</span>}
      </li>)}
    </ol></nav>}
    <div className="flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
      <div className="min-w-0 max-w-3xl space-y-1">
        <h1 className="break-words text-2xl font-semibold text-text-heading sm:text-[1.75rem] sm:leading-9">{title}</h1>
        {description && <p className="break-words text-[0.9375rem] text-text-muted">{description}</p>}
        {meta && <div className="flex flex-wrap items-center gap-2 pt-1">{meta}</div>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  </header>
}
