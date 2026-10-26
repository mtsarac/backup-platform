import { useMemo, useRef } from 'react'
import { ApiError } from '@/lib/api/client'

/** A failure whose outcome is unknown: the server may have recorded the request, so a retry must carry the same key. */
export const isUncertainError = (error: unknown) => error != null && (!(error instanceof ApiError) || error.status >= 500)

/**
 * One Idempotency-Key per request body. The same body keeps its key until the request settles with a known outcome
 * (success or a definite rejection); an uncertain failure keeps it so the retry is deduplicated. Any other body gets a new key.
 */
export function useIdempotencyKey() {
  const slot = useRef<{ body: string; key: string } | null>(null)
  return useMemo(() => ({
    keyFor(body: unknown) {
      const serialized = JSON.stringify(body)
      if (slot.current?.body !== serialized) slot.current = { body: serialized, key: crypto.randomUUID() }
      return slot.current.key
    },
    settle(error?: unknown) {
      if (!isUncertainError(error)) slot.current = null
    },
    reset() {
      slot.current = null
    },
  }), [])
}
