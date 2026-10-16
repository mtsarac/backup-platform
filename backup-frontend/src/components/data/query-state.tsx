import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Inbox } from 'lucide-react'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/api/failure-messages'

/** The one loading / error / empty frame for data that comes from the API. */
export function QueryState({ isPending, error, empty, children }: { isPending: boolean; error: unknown; empty?: boolean; children?: ReactNode }) {
  useLanguage()
  if (isPending) return <div role="status" aria-label={tx("Yükleniyor")} className="space-y-3 rounded-xl border border-border bg-surface p-5">
    <Skeleton className="h-5 w-1/3" /><Skeleton className="h-4 w-full" /><Skeleton className="h-4 w-4/5" /><span className="sr-only">{tx("Yükleniyor…")}</span>
  </div>
  if (error) return <Notice tone="error" title={tx("Veriler alınamadı")}>{errorMessage(error)}</Notice>
  if (empty) return <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Inbox} title={tx("Kayıt bulunamadı.")} /></div>
  return children
}
