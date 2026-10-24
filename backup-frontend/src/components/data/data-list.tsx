import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState, type ReactNode } from 'react'
import { Pagination } from '@/components/data/pagination'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
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
  const untagged = columns.filter(column => !column.card)
  const title = columns.find(column => column.card === 'title') ?? untagged[0]
  const fields = untagged.filter(column => column !== title).slice(0, KEY_FIELDS)
  const statuses = columns.filter(column => column.card === 'status')
  const actions = columns.filter(column => column.card === 'actions')
  return (
    <ul aria-label={caption} className={cn('space-y-3', className)}>
      {rows.map((row) => (
        <li key={keyExtractor(row)} className="space-y-3 rounded-xl border border-border bg-surface p-4 shadow-card">
          <div className="flex flex-wrap items-start justify-between gap-2">
            {title && <h3 className="min-w-0 break-words text-base font-semibold text-text-heading">{title.render(row)}</h3>}
            {statuses.length > 0 && <div className="flex flex-wrap items-center gap-2">{statuses.map(column => <span key={column.key}>{column.render(row)}</span>)}</div>}
          </div>
          {fields.length > 0 && (
            <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
              {fields.map((column) => (
                <div key={column.key} className="min-w-0">
                  <dt className="text-sm text-text-muted">{column.header}</dt>
                  <dd className="mt-0.5 break-words text-[0.9375rem] text-text">{column.render(row)}</dd>
                </div>
              ))}
            </dl>
          )}
          {actions.length > 0 && <div className="flex flex-wrap items-center gap-2 [&_button]:min-h-11 [&_button]:min-w-11">{actions.map(column => <span key={column.key}>{column.render(row)}</span>)}</div>}
        </li>
      ))}
    </ul>
  )
}

function Table<T>({ rows, columns, keyExtractor, className, caption }: ListProps<T>) {
  return (
    <div className={cn('overflow-x-auto rounded-xl border border-border bg-surface shadow-card', className)}>
      <table className="w-full border-collapse text-[0.9375rem]">
        {caption && <caption className="sr-only">{caption}</caption>}
        <thead>
          <tr className="border-b border-border bg-surface-2 text-left text-sm text-text-muted">
            {columns.map((column) => <th key={column.key} className="whitespace-nowrap px-4 py-3 font-medium">{column.header}</th>)}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={keyExtractor(row)} className="border-b border-border last:border-b-0 hover:bg-surface-2/70">
              {columns.map((column) => (
                <td key={column.key} className={cn('px-4 py-3 align-top text-text', column.wrap ? 'min-w-28 max-w-64 break-words' : 'whitespace-nowrap')}>{column.render(row)}</td>
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
          <FilterField label={search.label}><input type="search" aria-label={search.label} placeholder={search.label} className={filterControlClass} value={query} onChange={event => { setQuery(event.target.value); setPage(1) }} /></FilterField>
        </Filters>
      )}
      {visible.length === 0
        ? <div className={cn('rounded-xl border border-border bg-surface', className)}><EmptyState message={needle ? tx('Aramayla eşleşen kayıt yok.') : emptyMessage} /></div>
        : <List rows={visible} columns={columns} keyExtractor={keyExtractor} className={className} caption={caption} />}
      {search && matches.length > PAGE_SIZE && <Pagination label={tx("{0} sayfaları", { "0": search.label })} page={current} pageSize={PAGE_SIZE} totalCount={matches.length} onPageChange={setPage} />}
    </div>
  )
}
