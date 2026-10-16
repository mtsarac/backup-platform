import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'

interface PaginationProps {
  page: number
  pageSize: number
  totalCount: number
  onPageChange: (page: number) => void
  disabled?: boolean
  label?: string
}

function pageCount(totalCount: number, pageSize: number) {
  return Math.max(1, Math.ceil(totalCount / Math.max(1, pageSize)))
}

export function Pagination({ page, pageSize, totalCount, onPageChange, disabled = false, label = tx('Sayfalama') }: PaginationProps) {
  useLanguage()
  const lastPage = pageCount(totalCount, pageSize)
  return <nav aria-label={label} className="flex flex-wrap items-center justify-between gap-3 text-sm">
    <span className="text-text-muted">{totalCount}{' '}{tx("kayıt · Sayfa")}{' '}{page} / {lastPage}</span>
    <div className="flex gap-2">
      <Button variant="outline" disabled={disabled || page <= 1} onClick={() => onPageChange(page - 1)}>{tx("Önceki")}</Button>
      <Button variant="outline" disabled={disabled || page >= lastPage} onClick={() => onPageChange(page + 1)}>{tx("Sonraki")}</Button>
    </div>
  </nav>
}
