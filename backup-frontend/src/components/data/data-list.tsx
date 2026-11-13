import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Card, cardClass } from '@/components/ui/card'
import { useState, type ReactNode } from 'react'
import { Pagination } from '@/components/data/pagination'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters } from '@/components/ui/filters'
import { TextInput } from '@/components/ui/text-input'
import { useLargeScreen } from '@/hooks/use-large-screen'
import { cn } from '@/lib/utils'

/** What a column becomes in card mode. The title is the column tagged `title`, else the first untagged one; the next three untagged columns are the key fields. */
export type CardRole = 'title' | 'status' | 'actions' | 'hidden'

export interface Column<T> {
  readonly key: string
  readonly header: ReactNode
  readonly render: (row: T) => ReactNode
  /** Let long values (names, paths, schedules) wrap instead of forcing horizontal scrolling. */
  readonly wrap?: boolean
  /** `title` heads the card, `status` sits beside it, `actions` fills the footer, `hidden` is table-only (technical detail). */
  readonly card?: CardRole
}

interface DataListProps<T> {
  rows: readonly T[]
  columns: readonly Column<T>[]
  keyExtractor: (row: T) => string
  emptyMessage?: string
  className?: string
  /** Names the list for assistive tech (the table caption, the card list label). */
  caption?: string
  /** Client-side search and paging (25 per page) for a list that arrives complete from its endpoint. */
  search?: { label: string; text: (row: T) => string }
}

const PAGE_SIZE = 25
const KEY_FIELDS = 3

type ListProps<T> = Pick<DataListProps<T>, 'columns' | 'keyExtractor' | 'className' | 'caption'> & { rows: readonly T[] }

function Cards<T>({ rows, columns, keyExtractor, className, caption }: ListProps<T>) {
  useLanguage()
  const untagged = columns.filter(column => !column.card)
  const title = columns.find(column => column.card === 'title') ?? untagged[0]
  const visibleFields = untagged.filter(column => column !== title)
  const primaryFields = visibleFields.slice(0, KEY_FIELDS)
  const secondaryFields = visibleFields.slice(KEY_FIELDS)
  const statuses = columns.filter(column => column.card === 'status')
  const actions = columns.filter(column => column.card === 'actions')
  return (
    <ul aria-label={caption} className={cn('space-y-3', className)}>
      {rows.map((row) => (
        <Card as="li" key={keyExtractor(row)} className="space-y-3">
          <div className="flex flex-wrap items-start justify-between gap-2">
            {title && <h2 className="min-w-0 break-words text-base font-semibold text-text-heading">{title.render(row)}</h2>}
            {statuses.length > 0 && <div className="flex flex-wrap items-center gap-2">{statuses.map(column => <span key={column.key}>{column.render(row)}</span>)}</div>}
          </div>
          {primaryFields.length > 0 && (
            <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
              {primaryFields.map((column) => (
                <div key={column.key} className="min-w-0">
                  <dt className="text-sm text-text-muted">{column.header}</dt>
                  <dd className="mt-0.5 break-words text-body text-text">{column.render(row)}</dd>
                </div>
              ))}
            </dl>
          )}
          {secondaryFields.length > 0 && (
            <details className="group rounded-md border border-border/60 bg-surface-2/40 px-3 py-2 text-sm">
              <summary className="cursor-pointer select-none font-medium text-text-muted hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40">
                {tx('Diğer detaylar ({0})', { '0': String(secondaryFields.length) })}
              </summary>
              <dl className="mt-3 grid gap-x-6 gap-y-2 border-t border-border/40 pt-2 sm:grid-cols-2">
                {secondaryFields.map((column) => (
                  <div key={column.key} className="min-w-0">
                    <dt className="text-sm text-text-muted">{column.header}</dt>
                    <dd className="mt-0.5 break-words text-body text-text">{column.render(row)}</dd>
                  </div>
                ))}
              </dl>
            </details>
          )}
          {actions.length > 0 && <div className="flex flex-wrap items-center gap-2 [&_button]:min-h-11 [&_button]:min-w-11">{actions.map(column => <span key={column.key}>{column.render(row)}</span>)}</div>}
        </Card>
      ))}
    </ul>
  )
}

function Table<T>({ rows, columns, keyExtractor, className, caption }: ListProps<T>) {
  return (
    <div data-slot="data-table" className={cn(cardClass, 'overflow-x-auto', className)}>
      <table className="w-full border-collapse text-body">
        {caption && <caption className="sr-only">{caption}</caption>}
        <thead>
          <tr className="border-b border-border bg-surface-2 text-left text-sm text-text-muted">
            {columns.map((column) => <th key={column.key} className="whitespace-nowrap px-3 py-2 font-medium">{column.header}</th>)}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={keyExtractor(row)} className="border-b border-border last:border-b-0 hover:bg-surface-2/70">
              {columns.map((column) => (
                <td key={column.key} className={cn('px-3 py-2 align-top text-text [&_button]:min-h-8 [&_button]:py-1 [&_button]:text-sm pointer-coarse:[&_button]:min-h-11', column.card === 'actions' ? 'whitespace-nowrap [&_.flex-wrap]:flex-nowrap' : column.wrap ? 'min-w-40 max-w-80 break-words' : 'whitespace-nowrap')}>{column.render(row)}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * One list for every page: a table on large screens, cards below them so phones never scroll sideways.
 * Cards show the title, the status and a few key fields; mark the rest of a row `card: 'hidden'`.
 */
export function DataList<T>({ rows, columns, keyExtractor, emptyMessage = tx('Kayıt bulunamadı.'), className, caption, search }: DataListProps<T>) {
  useLanguage()
  const large = useLargeScreen()
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const needle = query.trim().toLocaleLowerCase(currentLocale())
  const matches = search && needle ? rows.filter(row => search.text(row).toLocaleLowerCase(currentLocale()).includes(needle)) : rows
  const current = search ? Math.min(page, Math.max(1, Math.ceil(matches.length / PAGE_SIZE))) : 1
  const visible = search ? matches.slice((current - 1) * PAGE_SIZE, current * PAGE_SIZE) : matches
  const List = large ? Table<T> : Cards<T>
  return (
    <div className="space-y-4">
      {search && (
        <Filters label={tx("{0} filtreleri", { "0": search.label })}>
          <FilterField label={search.label}><TextInput type="search" aria-label={search.label} placeholder={search.label} className="text-sm" value={query} onChange={event => { setQuery(event.target.value); setPage(1) }} /></FilterField>
        </Filters>
      )}
      {visible.length === 0
        ? <div className={cn(cardClass, className)}><EmptyState message={needle ? tx('Aramayla eşleşen kayıt yok.') : emptyMessage} /></div>
        : <List rows={visible} columns={columns} keyExtractor={keyExtractor} className={className} caption={caption} />}
      {search && matches.length > PAGE_SIZE && <Pagination label={tx("{0} sayfaları", { "0": search.label })} page={current} pageSize={PAGE_SIZE} totalCount={matches.length} onPageChange={setPage} />}
    </div>
  )
}
