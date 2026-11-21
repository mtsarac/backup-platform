import { tx, useLanguage } from '@/lib/i18n'
import { Card } from '@/components/ui/card'
import type { ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AccessDenied } from '@/components/auth/access-denied'
import { ErrorNotice } from '@/components/ui/notice'
import { Skeleton } from '@/components/ui/skeleton'
import { isForbidden, type ApiError } from '@/lib/api/client'

type EmptyProps = { empty?: undefined; emptyState?: undefined } | {
  /** True when the data arrived but holds nothing. */
  empty: boolean
  /** The screen's own empty state (an `EmptyState`: why it is empty and the one action). There is no generic one. */
  emptyState: ReactNode
}

/**
 * The one loading / error / empty frame for data that comes from the API. A failure always offers one retry: `onRetry`, or a reload of every failed query.
 * With `forbiddenMessage`, a 403 renders the permission wall (the message says who may see this) instead of a generic error.
 */
export function QueryState({ isPending, error, empty, emptyState, onRetry, forbiddenMessage, children }: {
  isPending: boolean; error: unknown; onRetry?: () => void; forbiddenMessage?: (error: ApiError) => string; children?: ReactNode
} & EmptyProps) {
  useLanguage()
  const client = useQueryClient()
  if (isPending) return <Card role="status" aria-label={tx("Yükleniyor")} className="space-y-3 p-5">
    <Skeleton className="h-5 w-1/3" /><Skeleton className="h-4 w-full" /><Skeleton className="h-4 w-4/5" /><span className="sr-only">{tx("Yükleniyor…")}</span>
  </Card>
  if (forbiddenMessage && isForbidden(error)) return <AccessDenied what={forbiddenMessage(error)} />
  if (error) return <ErrorNotice title={tx("Veriler alınamadı")} error={error} onRetry={onRetry ?? (() => { void client.refetchQueries({ predicate: query => query.state.status === 'error' }) })} />
  if (empty) return <Card padding="none">{emptyState}</Card>
  return children
}
