import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Inbox } from 'lucide-react'
import { useQueryClient } from '@tanstack/react-query'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNotice } from '@/components/ui/notice'
import { Skeleton } from '@/components/ui/skeleton'

/** The one loading / error / empty frame for data that comes from the API. A failure always offers one retry: `onRetry`, or a reload of every failed query. */
export function QueryState({ isPending, error, empty, onRetry, children }: { isPending: boolean; error: unknown; empty?: boolean; onRetry?: () => void; children?: ReactNode }) {
  useLanguage()
  const client = useQueryClient()
  if (isPending) return <div role="status" aria-label={tx("Yükleniyor")} className="space-y-3 rounded-xl border border-border bg-surface p-5">
    <Skeleton className="h-5 w-1/3" /><Skeleton className="h-4 w-full" /><Skeleton className="h-4 w-4/5" /><span className="sr-only">{tx("Yükleniyor…")}</span>
  </div>
  if (error) return <ErrorNotice title={tx("Veriler alınamadı")} error={error} onRetry={onRetry ?? (() => { void client.refetchQueries({ predicate: query => query.state.status === 'error' }) })} />
  if (empty) return <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Inbox} message={tx("Kayıt bulunamadı.")} /></div>
  return children
}
