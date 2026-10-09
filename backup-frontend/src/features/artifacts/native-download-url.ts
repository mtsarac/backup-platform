import { API_BASE_URL } from '@/lib/api/client'

export function nativeDownloadUrl(path: string | undefined): string | undefined {
  if (!path) return undefined
  try {
    const base = new URL(`${API_BASE_URL}/`, window.location.origin)
    const url = new URL(path, base)
    return url.origin === base.origin && url.pathname.startsWith(base.pathname) ? url.href : undefined
  } catch {
    return undefined
  }
}
