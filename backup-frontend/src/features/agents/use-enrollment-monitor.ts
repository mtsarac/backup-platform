import { tx } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { getAgent, getEnrollmentTokenStatus } from './api'
import type { AgentDetail } from './types'
import { errorMessage } from '@/lib/api/failure-messages'

type Ticket = { tokenId: string; expiresAt: string }
type Phase = 'Waiting' | 'Checking' | 'Enrolled' | 'Connecting' | 'Online' | 'Expired' | 'Revoked' | 'Consumed'

/** Only safe token metadata enters the monitor; polling never enters Query caches. */
export function useEnrollmentMonitor(tenantId: string, ticket: Ticket | null, clearSecret: () => void) {
  const [phase, setPhase] = useState<Phase>('Waiting')
  const [agent, setAgent] = useState<AgentDetail | null>(null)
  const [error, setError] = useState('')
  const [paused, setPaused] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const clear = useRef(clearSecret)
  useEffect(() => { clear.current = clearSecret }, [clearSecret])
  const enrollment = useRef<{ tokenId: string; agentId: string } | null>(null)
  const [scope, setScope] = useState({ tenantId, tokenId: ticket?.tokenId, attempt })
  if (scope.tenantId !== tenantId || scope.tokenId !== ticket?.tokenId || scope.attempt !== attempt) {
    setScope({ tenantId, tokenId: ticket?.tokenId, attempt })
    setPhase('Waiting'); setAgent(null); setError(''); setPaused(false)
  }
  useEffect(() => {
    if (!ticket) return
    let stopped = false
    let finished = false
    let enrolledId = enrollment.current?.tokenId === ticket.tokenId ? enrollment.current.agentId : null
    let failures = 0
    let polls = 0
    let timer: number | undefined
    let controller: AbortController | undefined
    const expires = Date.parse(ticket.expiresAt)
    let deadlinePassed = false
    const expire = () => {
      if (enrolledId || stopped || finished || deadlinePassed) return
      deadlinePassed = true
      clear.current(); setPhase('Checking')
      clearTimeout(timer)
      // Local time controls secret lifetime, never the authoritative enrollment result.
      // A consumption just before expiry may have happened while this tab was hidden.
      if (failures < 3 && polls < 120) void poll()
    }
    const expiryTimer = window.setTimeout(expire, Math.max(0, expires - Date.now()))
    async function poll() {
      if (stopped || finished || document.visibilityState === 'hidden' || (controller && !controller.signal.aborted)) return
      if (!enrolledId && Date.now() >= expires) {
        deadlinePassed = true
        clear.current(); setPhase('Checking')
      }
      controller = new AbortController()
      const signal = controller.signal
      try {
        polls++
        if (enrolledId) {
          const detail = await getAgent(tenantId, enrolledId, signal)
          if (signal.aborted || stopped) return
          setAgent(detail)
          setPhase(detail.connectivity === 'online' ? 'Online' : 'Connecting')
          if (detail.connectivity === 'online') { finished = true; clearTimeout(expiryTimer) }
        } else {
          const status = await getEnrollmentTokenStatus(tenantId, ticket!.tokenId, signal)
          if (signal.aborted || stopped) return
          if (status.state !== 'Active') {
            clear.current()
            if (status.state === 'Consumed' && status.agentId) {
              enrolledId = status.agentId
              enrollment.current = { tokenId: ticket!.tokenId, agentId: status.agentId }
              setPhase('Enrolled'); clearTimeout(expiryTimer)
            } else { setPhase(status.state); finished = true; clearTimeout(expiryTimer) }
          }
        }
        failures = 0; setError('')
      } catch (cause) {
        if (signal.aborted || stopped) return
        failures++; setError(errorMessage(cause))
      }
      finally { if (controller?.signal === signal) controller = undefined }
      if (stopped || finished || signal.aborted) return
      if (failures >= 3 || polls >= 120) {
        setPaused(true)
        if (polls >= 120) setError(tx('Otomatik takip durduruldu. Agent hizmetini ve backend bağlantısını kontrol edip tekrar deneyin.'))
        return
      }
      timer = window.setTimeout(() => { void poll() }, failures ? Math.min(30_000, 5_000 * 2 ** failures) : 5_000)
    }
    function visibility() {
      clearTimeout(timer)
      if (document.visibilityState === 'hidden') {
        controller?.abort()
        controller = undefined
      }
      else if (failures < 3 && polls < 120) void poll()
    }
    document.addEventListener('visibilitychange', visibility)
    void poll()
    return () => { stopped = true; controller?.abort(); clearTimeout(timer); clearTimeout(expiryTimer); document.removeEventListener('visibilitychange', visibility) }
  }, [tenantId, ticket, attempt])
  return { phase, agent, error, paused, retry: () => setAttempt(value => value + 1) }
}
