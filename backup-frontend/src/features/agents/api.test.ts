import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resetAntiforgery } from '@/lib/api/client'
import { createEnrollmentToken, getAgentDistribution } from './api'

let requests: Request[] = []
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  requests = []
  resetAntiforgery()
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init)
    requests.push(request)
    if (new URL(request.url).pathname === '/api/auth/antiforgery') return json({ requestToken: 'token', headerName: 'X-CSRF-TOKEN' })
    if (new URL(request.url).pathname.endsWith('/agent-enrollment-tokens')) {
      return json({ tokenId: 'tok-1', token: 'secret-token', expiresAt: '2026-10-28T00:00:00Z' })
    }
    return json({ serverUrl: 'https://backup.example.com', packages: { 'win-x64': true, 'linux-x64': true } })
  }))
})
afterEach(() => vi.unstubAllGlobals())

describe('agent API', () => {
  it('creates an enrollment token sending no lifetime in the request', async () => {
    const controller = new AbortController()
    const result = await createEnrollmentToken('tenant-a', controller.signal)
    expect(result.tokenId).toBe('tok-1')
    const req = requests.at(-1)!
    expect(req.method).toBe('POST')
    expect(new URL(req.url).pathname).toBe('/api/tenants/tenant-a/agent-enrollment-tokens')
    const body = await req.text()
    expect(body).toBe('')
  })

  it('reads agent distribution status anonymously', async () => {
    const dist = await getAgentDistribution()
    expect(dist.serverUrl).toBe('https://backup.example.com')
    expect(new URL(requests.at(-1)!.url).pathname).toBe('/api/agent-dist/status')
  })
})
