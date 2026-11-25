import { useMemo, useRef } from 'react'
import { sha256 } from '@noble/hashes/sha2.js'
import { bytesToHex, utf8ToBytes } from '@noble/hashes/utils.js'
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

const PREFIX = 'bp_idem:'
const digest = (body: string) => PREFIX + bytesToHex(sha256(utf8ToBytes(body)))

/**
 * Keys of create requests whose outcome may be lost with the page, kept per tab and addressed by a hash of the body
 * (tenant included), so the same request after a reload reuses its key and the body itself is never stored.
 * Cleared when the session ends so another user in the tab can never inherit one.
 */
export const pendingKeys = {
  // The storage can be unavailable (privacy mode, quota); the in-memory key still protects the open page.
  take(body: string) {
    try {
      const id = digest(body)
      const key = sessionStorage.getItem(id) ?? crypto.randomUUID()
      sessionStorage.setItem(id, key)
      return key
    } catch { return crypto.randomUUID() }
  },
  drop(body: string) {
    try { sessionStorage.removeItem(digest(body)) } catch { /* nothing persisted */ }
  },
  clear() {
    try {
      Object.keys(sessionStorage).filter(name => name.startsWith(PREFIX)).forEach(name => sessionStorage.removeItem(name))
    } catch { /* nothing persisted */ }
  },
}
