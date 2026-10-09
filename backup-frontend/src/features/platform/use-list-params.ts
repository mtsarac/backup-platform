import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

export const PLATFORM_PAGE_SIZE = 25

function positiveInt(value: string | null) {
  const parsed = Number(value)
  return Number.isInteger(parsed) && parsed >= 1 ? parsed : 1
}

/** URL-backed list state. Changing any filter drops the page unless `pageKey` itself is updated. */
export function useListParams(pageKey = 'page') {
  const [params, setParams] = useSearchParams()
  const page = positiveInt(params.get(pageKey))
  const get = (key: string) => params.get(key) ?? ''
  const update = useCallback((changes: Record<string, string | number | undefined>) => {
    setParams((current) => {
      const next = new URLSearchParams(current)
      const pageChange = pageKey in changes
      for (const [key, value] of Object.entries(changes)) {
        if (value === undefined || value === '' || (key === pageKey && value === 1)) next.delete(key)
        else next.set(key, String(value))
      }
      if (!pageChange) next.delete(pageKey)
      return next
    })
  }, [pageKey, setParams])
  return { page, get, update }
}
