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

export function DataTable<T>({
  rows,
  columns,
  keyExtractor,
  emptyMessage = 'Kayıt bulunamadı.',
  className,
  layout = 'table',
}: DataTableProps<T>) {
  if (layout === 'cards') {
    if (rows.length === 0) {
      return <p className="rounded-lg border border-border bg-surface px-4 py-8 text-center text-text-muted">{emptyMessage}</p>
    }
    return (
      <ul className={cn('space-y-3', className)}>
        {rows.map((row) => (
          <li key={keyExtractor(row)} className="rounded-lg border-2 border-border bg-surface p-4">
            <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
              {columns.map((column) => (
                <div key={column.key} className="min-w-0">
                  <dt className="text-sm font-medium text-text-muted">{column.header}</dt>
                  <dd className="mt-0.5 break-words text-base text-text">{column.render(row)}</dd>
                </div>
              ))}
            </dl>
          </li>
        ))}
      </ul>
    )
  }
  return (
    <div
      className={cn(
        'overflow-x-auto rounded-lg border border-border bg-surface',
        className
      )}
    >
      <table className="w-full border-collapse text-base">
        <thead>
          <tr className="border-b border-border bg-surface-2 text-left text-text-muted">
            {columns.map((column) => (
              <th key={column.key} className="whitespace-nowrap px-3 py-3 font-medium">
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td
                colSpan={columns.length}
                className="px-4 py-8 text-center text-text-muted"
              >
                {emptyMessage}
              </td>
            </tr>
          ) : (
            rows.map((row) => (
              <tr
                key={keyExtractor(row)}
                className="border-b border-border hover:bg-surface-2/50"
              >
                {columns.map((column) => (
                  <td key={column.key} className={cn('px-3 py-3 text-text', column.wrap ? 'min-w-28 max-w-52 break-words' : 'whitespace-nowrap')}>
                    {column.render(row)}
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
