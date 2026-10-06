import type { ReactNode } from 'react'
import { errorMessage } from '@/lib/api/failure-messages'

export function QueryState({ isPending, error, empty, children }: { isPending: boolean; error: unknown; empty?: boolean; children?: ReactNode }) {
  if (isPending) return <div role="status" className="rounded-lg border border-border bg-surface p-6 text-text-muted">Yükleniyor…</div>
  if (error) return <div role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-6 text-error">{errorMessage(error)}</div>
  if (empty) return <div className="rounded-lg border border-border bg-surface p-6 text-text-muted">Kayıt bulunamadı.</div>
  return children
}
