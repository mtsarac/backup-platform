import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Inbox } from 'lucide-react'
import { useQueryClient } from '@tanstack/react-query'
import { EmptyState } from '@/components/ui/empty-state'
import { AccessDenied } from '@/components/auth/access-denied'
import { ErrorNotice } from '@/components/ui/notice'
import { Skeleton } from '@/components/ui/skeleton'
import { isForbidden, type ApiError } from '@/lib/api/client'

/**
 * The one loading / error / empty frame for data that comes from the API. A failure always offers one retry: `onRetry`, or a reload of every failed query.
 * With `forbiddenMessage`, a 403 renders the permission wall (the message says who may see this) instead of a generic error.
 */
export function QueryState({ isPending, error, empty, onRetry, forbiddenMessage, children }: {
  isPending: boolean; error: unknown; empty?: boolean; onRetry?: () => void; forbiddenMessage?: (error: ApiError) => string; children?: ReactNode
}) {
  useLanguage()
  const client = useQueryClient()
  if (isPending) return <div role="status" aria-label={tx("Yükleniyor")} className="space-y-3 rounded-xl border border-border bg-surface p-5">
    <Skeleton className="h-5 w-1/3" /><Skeleton className="h-4 w-full" /><Skeleton className="h-4 w-4/5" /><span className="sr-only">{tx("Yükleniyor…")}</span>
  </div>
  if (forbiddenMessage && isForbidden(error)) return <AccessDenied what={forbiddenMessage(error)} />
  if (error) return <ErrorNotice title={tx("Veriler alınamadı")} error={error} onRetry={onRetry ?? (() => { void client.refetchQueries({ predicate: query => query.state.status === 'error' }) })} />
  if (empty) return <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Inbox} message={tx("Kayıt bulunamadı.")} /></div>
  return children
}
