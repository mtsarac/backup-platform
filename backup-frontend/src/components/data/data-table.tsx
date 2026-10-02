import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

export interface Column<T> {
  readonly key: string
  readonly header: ReactNode
  readonly render: (row: T) => ReactNode
}

interface DataTableProps<T> {
  rows: readonly T[]
  columns: readonly Column<T>[]
  keyExtractor: (row: T) => string
  emptyMessage?: string
  className?: string
}

export function DataTable<T>({
  rows,
  columns,
  keyExtractor,
  emptyMessage = 'Kayıt bulunamadı.',
  className,
}: DataTableProps<T>) {
  return (
    <div
      className={cn(
        'overflow-x-auto rounded-lg border border-border bg-surface',
        className
      )}
    >
      <table className="w-full border-collapse text-sm">
        <thead>
          <tr className="border-b border-border bg-surface-2 text-left text-text-muted">
            {columns.map((column) => (
              <th key={column.key} className="whitespace-nowrap px-4 py-3 font-medium">
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
                  <td key={column.key} className="whitespace-nowrap px-4 py-3 text-text">
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
