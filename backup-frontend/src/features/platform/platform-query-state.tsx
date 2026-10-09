import type { ReactNode } from 'react'
import { PlatformAccessDenied } from '@/components/auth/platform-access-denied'
import { QueryState } from '@/components/data/query-state'
import { ApiError } from '@/lib/api/client'

/** QueryState that renders the authorization state, not a generic error, when the API answers 403. */
export function PlatformQueryState({ isPending, error, empty, children }: { isPending: boolean; error: unknown; empty?: boolean; children?: ReactNode }) {
  if (error instanceof ApiError && error.status === 403) return <PlatformAccessDenied />
  return <QueryState isPending={isPending} error={error} empty={empty}>{children}</QueryState>
}
