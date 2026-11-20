import { tx, useLanguage } from '@/lib/i18n'
import { Card, cardClass } from '@/components/ui/card'
import type { ReactNode } from 'react'
import { ArrowDown, ArrowUp, ChevronsUpDown } from 'lucide-react'
import { EmptyState } from '@/components/ui/empty-state'
import { Select } from '@/components/ui/text-input'
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
  /** Makes the header a sort button (the table) and an option of the sort menu (cards); needs `sort` on the list. The header must be a string. */
  readonly sortable?: boolean
}

export type SortDirection = 'asc' | 'desc'
/** Build it with `sortProps(listState)`. `first` is the direction each key sorts in when chosen first. */
export interface SortState { key: string; direction: SortDirection; first: Record<string, SortDirection>; onChange: (key: string, direction: SortDirection) => void }

interface DataListProps<T> {
  rows: readonly T[]
  columns: readonly Column<T>[]
  keyExtractor: (row: T) => string
  /** Why the list is empty and what to do about it: one sentence, written for the screen. There is no generic default. */
  emptyMessage: string
  onClearFilters?: () => void
  className?: string
  /** Names the list for assistive tech (the table caption, the card list label). */
  caption?: string
  /** The caller sorts `rows`; this only shows and changes the order. */
  sort?: SortState
}

const KEY_FIELDS = 3

type ListProps<T> = Pick<DataListProps<T>, 'columns' | 'keyExtractor' | 'className' | 'caption' | 'sort'> & { rows: readonly T[] }

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
            <details className="group rounded-lg border border-border bg-surface-2 px-3 py-2 text-sm">
              <summary className="cursor-pointer select-none font-medium text-text-muted hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                {tx('Diğer detaylar ({0})', { '0': String(secondaryFields.length) })}
              </summary>
              <dl className="mt-3 grid gap-x-6 gap-y-2 border-t border-border pt-2 sm:grid-cols-2">
                {secondaryFields.map((column) => (
                  <div key={column.key} className="min-w-0">
                    <dt className="text-sm text-text-muted">{column.header}</dt>
                    <dd className="mt-0.5 break-words text-body text-text">{column.render(row)}</dd>
                  </div>
                ))}
              </dl>
            </details>
          )}
          {actions.length > 0 && <div className="flex flex-wrap items-center gap-2">{actions.map(column => <span key={column.key}>{column.render(row)}</span>)}</div>}
        </Card>
      ))}
    </ul>
  )
}

function SortButton<T>({ column, sort }: { column: Column<T>; sort: SortState }) {
  const active = sort.key === column.key
  const Icon = !active ? ChevronsUpDown : sort.direction === 'asc' ? ArrowUp : ArrowDown
  const next = active ? (sort.direction === 'asc' ? 'desc' : 'asc') : sort.first[column.key]
  return <button type="button" className="inline-flex items-center gap-1 rounded-sm font-medium hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" onClick={() => sort.onChange(column.key, next)}>
    {column.header}<Icon aria-hidden="true" className={cn('size-3.5', !active && 'opacity-50')} />
  </button>
}

/** The sort select shown instead of the table's header buttons; also for a list of cards that is not a `DataList`. */
export function SortMenu({ options, sort }: { options: readonly { key: string; label: string }[]; sort: SortState }) {
  return <label className="block space-y-1 text-sm font-medium text-text-heading">{tx('Sırala')}
    <Select value={`${sort.key}:${sort.direction}`} onChange={event => { const [key, direction] = event.target.value.split(':'); sort.onChange(key, direction as SortDirection) }}>
      {options.flatMap(option => (['asc', 'desc'] as const).map(direction => <option key={`${option.key}:${direction}`} value={`${option.key}:${direction}`}>{direction === 'asc' ? tx('{0}, artan', { '0': option.label }) : tx('{0}, azalan', { '0': option.label })}</option>))}
    </Select>
  </label>
}

function Table<T>({ rows, columns, keyExtractor, className, caption, sort }: ListProps<T>) {
  return (
    <div data-slot="data-table" className={cn(cardClass, 'overflow-x-auto', className)}>
      <table className="w-full border-collapse text-body">
        {caption && <caption className="sr-only">{caption}</caption>}
        <thead>
          <tr className="border-b border-border bg-surface-2 text-left text-sm text-text-muted">
            {columns.map((column) => <th key={column.key} aria-sort={column.sortable && sort ? sort.key === column.key ? (sort.direction === 'asc' ? 'ascending' : 'descending') : 'none' : undefined} className="whitespace-nowrap px-3 py-2 font-medium">
              {column.sortable && sort ? <SortButton column={column} sort={sort} /> : column.header}
            </th>)}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={keyExtractor(row)} className="border-b border-border last:border-b-0 hover:bg-surface-2">
              {columns.map((column) => (
                <td key={column.key} className={cn('px-3 py-2 align-top text-text [&_button]:min-h-control [&_button]:py-1 [&_button]:text-sm', column.card === 'actions' ? 'whitespace-nowrap [&_.flex-wrap]:flex-nowrap' : column.wrap ? 'min-w-40 max-w-80 break-words' : 'whitespace-nowrap')}>{column.render(row)}</td>
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
export function DataList<T>({ rows, columns, keyExtractor, emptyMessage, onClearFilters, className, caption, sort }: DataListProps<T>) {
  useLanguage()
  const large = useLargeScreen()
  const List = large ? Table<T> : Cards<T>
  return (
    <div className="space-y-4">
      {rows.length === 0
        ? <div className={cn(cardClass, className)}><EmptyState message={emptyMessage} onClearFilters={onClearFilters} /></div>
        : <>
          {!large && sort && <SortMenu options={columns.filter(column => column.sortable).map(column => ({ key: column.key, label: String(column.header) }))} sort={sort} />}
          <List rows={rows} columns={columns} keyExtractor={keyExtractor} className={className} caption={caption} sort={sort} />
        </>}
    </div>
  )
}
