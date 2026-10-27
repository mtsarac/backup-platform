import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

export const PLATFORM_PAGE_SIZE = 25

function positiveInt(value: string | null) {
  const parsed = Number(value)
  return Number.isInteger(parsed) && parsed >= 1 ? parsed : 1
}

/** URL-backed list state. Changing any filter drops the page unless `pageKey` itself is updated. `replace` rewrites the current history entry (typing in a search box). */
export function useListParams(pageKey = 'page') {
  const [params, setParams] = useSearchParams()
  const page = positiveInt(params.get(pageKey))
  const get = (key: string) => params.get(key) ?? ''
  const update = useCallback((changes: Record<string, string | number | undefined>, options?: { replace?: boolean }) => {
    setParams((current) => {
      const next = new URLSearchParams(current)
      const pageChange = pageKey in changes
      for (const [key, value] of Object.entries(changes)) {
        if (value === undefined || value === '' || (key === pageKey && value === 1)) next.delete(key)
        else next.set(key, String(value))
      }
      if (!pageChange) next.delete(pageKey)
      return next
    }, options)
  }, [pageKey, setParams])
  return { page, get, update }
}

/** A URL value if it is one of `allowed`, else undefined (an unknown or hand-edited value is ignored, never sent to the API). */
export function oneOf<T extends string>(value: string, allowed: readonly T[]): T | undefined {
  return allowed.find((item) => item === value)
}

/** A `YYYY-MM-DD` URL value for a date input, or '' when it is not a real day. */
export function dayParam(value: string): string {
  return /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value)) ? value : ''
}
