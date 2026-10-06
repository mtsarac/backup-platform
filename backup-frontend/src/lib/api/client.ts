import ky from 'ky'

export const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? '/api').replace(/\/$/, '')
const http = ky.create({ credentials: 'include', retry: 0, timeout: 30_000, throwHttpErrors: false, headers: { Accept: 'application/json' } })
type ErrorBody = { error?: string; code?: string; reason?: string; field?: string; errors?: Record<string, string[]>; activeRunId?: string }
export class ApiError extends Error {
  readonly status: number
  readonly code?: string
  readonly reason?: string
  readonly field?: string
  readonly fields: Record<string, string>
  readonly activeRunId?: string
  constructor(status: number, body: ErrorBody = {}) {
    super(`API request failed (${status})`)
    this.name = 'ApiError'
    this.status = status
    this.code = body.code ?? body.error
    this.reason = body.reason
    this.field = body.field
    this.activeRunId = body.activeRunId
    // Do not surface backend exception/message text. Validation values are stable codes.
    this.fields = Object.fromEntries(Object.entries(body.errors ?? {}).map(([field]) => [field, 'invalid_request']))
    if (body.field) this.fields[body.field] = body.code ?? 'invalid_request'
  }
}
let csrf: { requestToken: string; headerName: string } | undefined
let csrfRequest: Promise<typeof csrf> | undefined
let csrfGeneration = 0
const expiredListeners = new Set<() => void>()
export function subscribeSessionExpired(listener: () => void) {
  expiredListeners.add(listener)
  return () => { expiredListeners.delete(listener) }
}
export function resetAntiforgery() {
  csrfGeneration++
  csrf = undefined
  csrfRequest = undefined
}
async function getAntiforgery() {
  if (csrf) return csrf
  if (!csrfRequest) {
    const generation = csrfGeneration
    csrfRequest = (async () => {
      const response = await http(`${API_BASE_URL}/auth/antiforgery`)
      if (!response.ok) throw new ApiError(response.status)
      const token = await response.json<{ requestToken: string; headerName: string }>()
      if (generation === csrfGeneration) csrf = token
      return token
    })().finally(() => { if (generation === csrfGeneration) csrfRequest = undefined })
  }
  return csrfRequest
}
export interface RequestOptions { method?: string; json?: unknown; signal?: AbortSignal; headers?: Record<string, string> }
export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  if (path.startsWith('/') || path.includes('://') || path.includes('..')) throw new Error('API path must be relative')
  const method = (options.method ?? 'GET').toUpperCase()
  const headers = { ...options.headers }
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    const token = await getAntiforgery()
    if (!token) throw new Error('Antiforgery token unavailable')
    headers[token.headerName] = token.requestToken
  }
  const response = await http(`${API_BASE_URL}/${path}`, { ...options, method, headers })
  if (!response.ok) {
    const body: ErrorBody = await response.json<ErrorBody>().catch(() => ({}))
    if (response.status === 401 && path !== 'auth/login') {
      resetAntiforgery()
      expiredListeners.forEach(listener => listener())
    }
    throw new ApiError(response.status, body)
  }
  if (response.status === 204) return undefined as T
  return response.json<T>()
}
export function tenantPath(tenantId: string, resource: string) {
  if (!tenantId || /[/?#]/.test(tenantId) || resource.startsWith('/') || resource.includes('..')) throw new Error('Invalid tenant route')
  return `tenants/${encodeURIComponent(tenantId)}/${resource}`
}
