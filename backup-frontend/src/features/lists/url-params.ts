import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

/** Rows per page of every list, unless its config says otherwise. */
export const LIST_PAGE_SIZE = 25

/** The query string as `get` / `update`: for page state that is not a list (a tab, a scope). Lists use `useListState`, which is built on this. `replace` rewrites the current history entry. */
export function useUrlParams() {
  const [params, setParams] = useSearchParams()
  const get = (key: string) => params.get(key) ?? ''
  const update = useCallback((changes: Record<string, string | number | undefined>, options?: { replace?: boolean }) => {
    setParams((current) => {
      const next = new URLSearchParams(current)
      for (const [key, value] of Object.entries(changes)) {
        if (value === undefined || value === '') next.delete(key)
        else next.set(key, String(value))
      }
      return next
    }, options)
  }, [setParams])
  return { get, update }
}

/** A URL value if it is one of `allowed`, else undefined (an unknown or hand-edited value is ignored, never sent to the API). */
export function oneOf<T extends string>(value: string, allowed: readonly T[]): T | undefined {
  return allowed.find((item) => item === value)
}

/** A `YYYY-MM-DD` URL value for a date input, or '' when it is not a real day. */
export function dayParam(value: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return ''
  const date = new Date(value)
  return !Number.isNaN(date.getTime()) && date.toISOString().slice(0, 10) === value ? value : ''
}
