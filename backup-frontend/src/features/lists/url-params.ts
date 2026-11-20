import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

/** Rows per page of every list, unless its config says otherwise. */
export const LIST_PAGE_SIZE = 25

/** The highest page a URL can ask for. A hand-edited `page=2147483648` is capped here, never sent to the API; a page past the last one falls back to the last (see `ListPager`). */
export const MAX_PAGE = 100_000

/** A `page` URL value as a page number: a whole number from 1 to `MAX_PAGE`, else 1. */
export function pageParam(value: string): number {
  const page = Number(value)
  return Number.isInteger(page) && page >= 1 ? Math.min(page, MAX_PAGE) : 1
}

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

/** A typed day is complete once its year has four digits: a keyboard passes through 0002, 0020 and 0202 on the way to 2026, and none of those is a date anyone means. */
export function isCompleteDay(value: string): boolean {
  return dayParam(value) !== '' && value >= '1000-01-01'
}

/** A start day after the end day: the list must not ask the API for it (it would answer with an error); the date fields say so instead. */
export const isInvertedRange = (from: string, to: string) => Boolean(from && to && from > to)
