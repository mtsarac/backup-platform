// HTTPS sink (:443) for S2 webhook and generic-HTTP SMS deliveries, with an HTTP inspection API (:8080)
import { readFileSync } from 'node:fs'

type Recorded = { id: number; at: string; method: string; path: string; status: number; headers: Record<string, string>; body: string }
const requests: Recorded[] = []
let seq = 0
let failEndpoint = true

const cert = readFileSync('/certs/public.crt', 'utf8')
const key = readFileSync('/certs/private.key', 'utf8')

Bun.serve({
  port: 443,
  tls: { cert, key },
  async fetch(req) {
    const url = new URL(req.url)
    const body = await req.text()
    const headers: Record<string, string> = {}
    req.headers.forEach((v, k) => { headers[k.toLowerCase()] = v })
    if (url.pathname === '/fail' && failEndpoint) {
      requests.push({ id: ++seq, at: new Date().toISOString(), method: req.method, path: url.pathname, status: 404, headers, body })
      return new Response('not found', { status: 404 })
    }
    requests.push({ id: ++seq, at: new Date().toISOString(), method: req.method, path: url.pathname, status: 200, headers, body })
    return Response.json({ ok: true })
  },
})

Bun.serve({
  port: 8080,
  async fetch(req) {
    const url = new URL(req.url)
    if (req.method === 'GET' && url.pathname === '/requests') return Response.json({ failEndpoint, items: requests })
    if (req.method === 'DELETE' && url.pathname === '/requests') { requests.length = 0; return new Response(null, { status: 204 }) }
    if (req.method === 'POST' && url.pathname === '/mode') {
      failEndpoint = url.searchParams.get('fail') !== 'false'
      return Response.json({ failEndpoint })
    }
    return new Response('ok')
  },
})

console.log('s2-webhook listening on :443 (TLS) and :8080 (control)')
