import { tx } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { getAgent, getEnrollmentTokenStatus } from './api'
import type { AgentDetail } from './types'
import { type ErrorDescription, describeError } from '@/lib/api/failure-messages'

type Ticket = { tokenId: string; expiresAt: string }
type Phase = 'Waiting' | 'Checking' | 'Enrolled' | 'Connecting' | 'Online' | 'Expired' | 'Revoked' | 'Consumed'

/** Only safe token metadata enters the monitor; polling never enters Query caches. */
export function useEnrollmentMonitor(tenantId: string, ticket: Ticket | null, clearSecret: () => void) {
  const [phase, setPhase] = useState<Phase>('Waiting')
  const [agent, setAgent] = useState<AgentDetail | null>(null)
  const [error, setError] = useState<ErrorDescription | null>(null)
  const [paused, setPaused] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const clear = useRef(clearSecret)
  useEffect(() => { clear.current = clearSecret }, [clearSecret])
  const enrollment = useRef<{ tokenId: string; agentId: string } | null>(null)
  const [scope, setScope] = useState({ tenantId, tokenId: ticket?.tokenId, attempt })
  if (scope.tenantId !== tenantId || scope.tokenId !== ticket?.tokenId || scope.attempt !== attempt) {
    setScope({ tenantId, tokenId: ticket?.tokenId, attempt })
    setPhase('Waiting'); setAgent(null); setError(null); setPaused(false)
  }
  useEffect(() => {
    if (!ticket) return
    // Mutated from the async `poll` and the cleanup; an object keeps those writes out of the "reassign in async function" lint rule.
    const run = {
      stopped: false,
      finished: false,
      enrolledId: enrollment.current?.tokenId === ticket.tokenId ? enrollment.current.agentId : null as string | null,
      failures: 0,
      polls: 0,
      timer: undefined as number | undefined,
      controller: undefined as AbortController | undefined,
      deadlinePassed: false,
    }
    const expires = Date.parse(ticket.expiresAt)
    const expire = () => {
      if (run.enrolledId || run.stopped || run.finished || run.deadlinePassed) return
      run.deadlinePassed = true
      clear.current(); setPhase('Checking')
      clearTimeout(run.timer)
      // Local time controls secret lifetime, never the authoritative enrollment result.
      // A consumption just before expiry may have happened while this tab was hidden.
      if (run.failures < 3 && run.polls < 120) void poll()
    }
    const expiryTimer = window.setTimeout(expire, Math.max(0, expires - Date.now()))
    async function poll() {
      if (run.stopped || run.finished || document.visibilityState === 'hidden' || (run.controller && !run.controller.signal.aborted)) return
      if (!run.enrolledId && Date.now() >= expires) {
        run.deadlinePassed = true
        clear.current(); setPhase('Checking')
      }
      run.controller = new AbortController()
      const signal = run.controller.signal
      try {
        run.polls++
        if (run.enrolledId) {
          const detail = await getAgent(tenantId, run.enrolledId, signal)
          if (signal.aborted || run.stopped) return
          setAgent(detail)
          setPhase(detail.connectivity === 'online' ? 'Online' : 'Connecting')
          if (detail.connectivity === 'online') { run.finished = true; clearTimeout(expiryTimer) }
        } else {
          const status = await getEnrollmentTokenStatus(tenantId, ticket!.tokenId, signal)
          if (signal.aborted || run.stopped) return
          if (status.state !== 'Active') {
            clear.current()
            if (status.state === 'Consumed' && status.agentId) {
              run.enrolledId = status.agentId
              enrollment.current = { tokenId: ticket!.tokenId, agentId: status.agentId }
              setPhase('Enrolled'); clearTimeout(expiryTimer)
            } else { setPhase(status.state); run.finished = true; clearTimeout(expiryTimer) }
          }
        }
        run.failures = 0; setError(null)
      } catch (cause) {
        if (signal.aborted || run.stopped) return
        run.failures++; setError(describeError(cause))
      }
      finally { if (run.controller?.signal === signal) run.controller = undefined }
      if (run.stopped || run.finished || signal.aborted) return
      if (run.failures >= 3 || run.polls >= 120) {
        setPaused(true)
        if (run.polls >= 120) setError({ what: tx('Otomatik takip durduruldu. Agent hizmetini ve backend bağlantısını kontrol edip tekrar deneyin.'), dataImpact: tx("Kayıt token'ınız etkilenmedi; süresi dolana kadar kullanılabilir."), action: tx('Takibi tekrar deneyin.'), technical: [] })
        return
      }
      run.timer = window.setTimeout(() => { void poll() }, run.failures ? Math.min(30_000, 5_000 * 2 ** run.failures) : 5_000)
    }
    function visibility() {
      clearTimeout(run.timer)
      if (document.visibilityState === 'hidden') {
        run.controller?.abort()
        run.controller = undefined
      }
      else if (run.failures < 3 && run.polls < 120) void poll()
    }
    document.addEventListener('visibilitychange', visibility)
    void poll()
    return () => { run.stopped = true; run.controller?.abort(); clearTimeout(run.timer); clearTimeout(expiryTimer); document.removeEventListener('visibilitychange', visibility) }
  }, [tenantId, ticket, attempt])
  return { phase, agent, error, paused, retry: () => setAttempt(value => value + 1) }
}
