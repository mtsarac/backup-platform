import type { ReactNode } from 'react'
import { AccessDenied } from '@/components/auth/access-denied'
import { QueryState } from '@/components/data/query-state'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

export function SupportQueryState({ isPending, error, children }: { isPending: boolean; error: unknown; children?: ReactNode }) {
  if (error instanceof ApiError && error.status === 403) return <AccessDenied what={errorMessage(error)} />
  return <QueryState isPending={isPending} error={error}>{children}</QueryState>
}
