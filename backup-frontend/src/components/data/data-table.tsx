import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

export interface Column<T> {
  readonly key: string
  readonly header: ReactNode
  readonly render: (row: T) => ReactNode
  /** Let long values (names, paths, schedules) wrap instead of forcing horizontal scrolling. */
  readonly wrap?: boolean
}

interface DataTableProps<T> {
  rows: readonly T[]
  columns: readonly Column<T>[]
  keyExtractor: (row: T) => string
  emptyMessage?: string
  className?: string
  /** 'cards' renders each row as a roomy stacked card instead of a table row. */
  layout?: 'table' | 'cards'
}

const empty = (message: string) => <p className="px-4 py-10 text-center text-text-muted">{message}</p>

/**
 * A real table from `md` up. Below that each row becomes a labelled card (the header is read from the column,
 * not duplicated in the DOM), so phones never need horizontal scrolling. Explicit roles keep the table semantics when the layout turns block.
 */
export function DataTable<T>({ rows, columns, keyExtractor, emptyMessage = tx('Kayıt bulunamadı.'), className, layout = 'table' }: DataTableProps<T>) {
  useLanguage()
  if (layout === 'cards') {
    if (rows.length === 0) return <div className="rounded-xl border border-border bg-surface">{empty(emptyMessage)}</div>
    return (
      <ul className={cn('space-y-3', className)}>
        {rows.map((row) => (
          <li key={keyExtractor(row)} className="rounded-xl border border-border bg-surface p-4 shadow-card">
            <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
              {columns.map((column) => (
                <div key={column.key} className="min-w-0">
                  <dt className="text-sm text-text-muted">{column.header}</dt>
                  <dd className="mt-0.5 break-words text-[0.9375rem] text-text">{column.render(row)}</dd>
                </div>
              ))}
            </dl>
          </li>
        ))}
      </ul>
    )
  }
  return (
    <div className={cn('overflow-x-auto rounded-xl border border-border bg-surface shadow-card', className)}>
      <table role="table" className="w-full border-collapse text-[0.9375rem] max-md:block">
        <thead role="rowgroup" className="max-md:sr-only">
          <tr role="row" className="border-b border-border bg-surface-2 text-left text-sm text-text-muted">
            {columns.map((column) => (
              <th key={column.key} role="columnheader" className="whitespace-nowrap px-4 py-3 font-medium">{column.header}</th>
            ))}
          </tr>
        </thead>
        <tbody role="rowgroup" className="max-md:block">
          {rows.length === 0 ? (
            <tr role="row" className="max-md:block"><td role="cell" colSpan={columns.length} className="max-md:block">{empty(emptyMessage)}</td></tr>
          ) : (
            rows.map((row) => (
              <tr role="row" key={keyExtractor(row)} className="border-b border-border last:border-b-0 hover:bg-surface-2/70 max-md:block max-md:space-y-1.5 max-md:px-4 max-md:py-3">
                {columns.map((column) => (
                  <td role="cell" key={column.key} data-label={typeof column.header === 'string' ? column.header : undefined}
                    className={cn('px-4 py-3 align-top text-text max-md:flex max-md:min-w-0 max-md:max-w-none max-md:gap-3 max-md:px-0 max-md:py-0.5 max-md:before:w-28 max-md:before:shrink-0 max-md:before:text-sm max-md:before:text-text-muted max-md:before:content-[attr(data-label)]',
                      column.wrap ? 'min-w-28 max-w-64 break-words' : 'whitespace-nowrap max-md:whitespace-normal')}>
                    <span className="min-w-0 break-words">{column.render(row)}</span>
                  </td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  )
}
