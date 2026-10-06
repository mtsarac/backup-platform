import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiRequest, ApiError, resetAntiforgery, subscribeSessionExpired, tenantPath } from './client'
import { errorMessage, failureMessage } from './failure-messages'

const requests: Request[] = []
let handle: (request: Request) => Response | Promise<Response>
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
beforeEach(() => {
  requests.length = 0
  resetAntiforgery()
  handle = request => new URL(request.url).pathname.endsWith('/auth/antiforgery') ? json({ requestToken: 'csrf-identity', headerName: 'X-CSRF-TOKEN' }) : json({ ok: true })
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input : new URL(String(input), 'http://localhost')
    const request = new Request(url, init)
    requests.push(request)
    return handle(request)
  }))
})
afterEach(() => vi.unstubAllGlobals())
describe('browser API contract', () => {
  it('includes cookies and sends identity-bound antiforgery automatically on unsafe requests', async () => {
    await apiRequest('auth/login', { method: 'POST', json: { loginIdentifier: 'operator', password: 'not-persisted' } })
    const login = requests.find(request => request.url.endsWith('/auth/login'))!
    expect(login.credentials).toBe('include')
    expect(login.headers.get('X-CSRF-TOKEN')).toBe('csrf-identity')
    expect(await login.json()).toEqual({ loginIdentifier: 'operator', password: 'not-persisted' })
    resetAntiforgery()
    handle = request => request.url.endsWith('/auth/antiforgery') ? json({ requestToken: 'authenticated-csrf', headerName: 'X-CSRF-TOKEN' }) : new Response(null, { status: 204 })
    await apiRequest('auth/logout', { method: 'POST' })
    expect(requests.at(-1)?.headers.get('X-CSRF-TOKEN')).toBe('authenticated-csrf')
  })
  it('shares concurrent antiforgery initialization but does not retry mutations', async () => {
    await Promise.all([apiRequest('first', { method: 'POST' }), apiRequest('second', { method: 'DELETE' })])
    expect(requests.filter(request => request.url.endsWith('/auth/antiforgery'))).toHaveLength(1)
    handle = () => json({ error: 'InternalError', message: 'secret exception stack' }, 500)
    await expect(apiRequest('mutation', { method: 'POST' })).rejects.toBeInstanceOf(ApiError)
    expect(requests.filter(request => request.url.endsWith('/mutation'))).toHaveLength(1)
  })
  it('distinguishes unauthorized from expired session without exposing exception text', async () => {
    const expired = vi.fn()
    const unsubscribe = subscribeSessionExpired(expired)
    handle = () => json({ message: 'raw stack trace' }, 403)
    let failure: unknown
    try { await apiRequest('me') } catch (error) { failure = error }
    expect(errorMessage(failure)).toBe('Bu işlem için yetkiniz yok.')
    expect(expired).not.toHaveBeenCalled()
    handle = () => json({}, 401)
    await expect(apiRequest('me')).rejects.toMatchObject({ status: 401 })
    expect(expired).toHaveBeenCalledOnce()
    unsubscribe()
  })
  it('passes AbortSignal so old tenant requests can be cancelled', async () => {
    const abort = new AbortController()
    handle = request => {
      const { promise, reject } = Promise.withResolvers<Response>()
      request.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
      if (request.signal.aborted) reject(new DOMException('Aborted', 'AbortError'))
      return promise
    }
    const request = apiRequest(tenantPath('tenant-a', 'backup-runs'), { signal: abort.signal })
    abort.abort()
    await expect(request).rejects.toMatchObject({ name: 'AbortError' })
  })
  it('maps unknown stable failure codes but never backend exceptions', () => {
    expect(failureMessage('FutureFailure')).toBe('İşlem başarısız oldu (FutureFailure).')
    expect(failureMessage('System.Exception: password=secret')).toBe('İşlem başarısız oldu.')
    expect(failureMessage('attempts_exhausted')).toContain('attempt')
    expect(failureMessage('storage_not_configured')).toContain('Storage')
  })
  it('keeps validation errors field-addressable and prevents tenant route escape', async () => {
    handle = () => json({ error: 'invalid_schedule', field: 'cronExpression', code: 'invalid_cron', message: 'parser internals' }, 400)
    await expect(apiRequest('job')).rejects.toMatchObject({ fields: { cronExpression: 'invalid_cron' } })
    expect(() => tenantPath('../foreign', 'backup-runs')).toThrow()
  })
})
