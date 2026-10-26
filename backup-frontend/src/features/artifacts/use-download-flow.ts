import { useEffect, useRef, useState } from 'react'
import { useQuery, type QueryKey } from '@tanstack/react-query'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import type { DownloadStatus, PreparedDownload } from './api'
import { nativeDownloadUrl } from './native-download-url'

const FINAL: readonly DownloadStatus['state'][] = ['Completed', 'Failed', 'Cancelled']

/** A prepared download whose link is dropped as soon as it has been used: the link is a credential. */
export type DownloadSession = Omit<PreparedDownload, 'downloadUrl'> & { downloadUrl?: string }

/** Only a relative, query-free path on this API may be navigated; anything else is refused. */
export function safeDownloadUrl(path: string): string | undefined {
  return path.startsWith('/') && !path.startsWith('//') && !/[?#]/.test(path) ? nativeDownloadUrl(path) : undefined
}

/**
 * The one download flow behind tenant, support and emergency downloads: prepare, poll the transfer, let the browser save the file
 * (or hand a direct transfer to its button). The link is never put in the query or mutation cache or in rendered state: a browser
 * navigation link waits in a ref until it is used once, a direct transfer's link stays in state only until the transfer is over.
 */
export function useDownloadFlow({ active = true, prepare, fetchStatus, statusKey, onPrepared }: {
  /** False when the caller may no longer download (permission or scope changed): nothing starts and nothing is navigated. */
  active?: boolean
  prepare: () => Promise<PreparedDownload>
  fetchStatus: (sessionId: string, signal: AbortSignal) => Promise<DownloadStatus>
  statusKey: (sessionId: string | undefined) => QueryKey
  /** The server has accepted the request (an emergency grant is spent at this point). */
  onPrepared?: () => void
}) {
  const visible = useVisibleTab()
  const mounted = useRef(true)
  const submitting = useRef(false)
  const link = useRef<string>(undefined)
  const [session, setSession] = useState<DownloadSession>()
  const [unsafe, setUnsafe] = useState(false)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<unknown>()
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])

  const status = useQuery({
    queryKey: statusKey(session?.sessionId),
    queryFn: ({ signal }) => fetchStatus(session!.sessionId, signal),
    enabled: active && Boolean(session) && !unsafe,
    retry: false,
    refetchInterval: query => visible && !query.state.error && !FINAL.includes(query.state.data?.state ?? 'Preparing') ? 1000 : false,
  })
  const state = status.data?.state
  const forget = () => { link.current = undefined; setSession(current => current && { ...current, downloadUrl: undefined }) }

  useEffect(() => {
    if (!active || !session || session.direct || !link.current || state !== 'Ready') return
    const anchor = document.createElement('a')
    anchor.href = link.current
    anchor.download = session.filename
    link.current = undefined
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  }, [active, session, state])

  async function start() {
    if (submitting.current || !active) return
    submitting.current = true
    link.current = undefined
    setError(undefined); setUnsafe(false); setSession(undefined); setPending(true)
    try {
      const prepared = await prepare()
      onPrepared?.()
      if (!mounted.current) return
      const safe = Boolean(safeDownloadUrl(prepared.downloadUrl))
      setUnsafe(!safe)
      link.current = safe && !prepared.direct ? prepared.downloadUrl : undefined
      setSession({ ...prepared, downloadUrl: safe && prepared.direct ? prepared.downloadUrl : undefined })
    } catch (cause) {
      if (mounted.current) setError(cause)
    } finally {
      submitting.current = false
      if (mounted.current) setPending(false)
    }
  }

  const busy = pending || Boolean(session && !unsafe && !status.error && (state === undefined || !FINAL.includes(state)))
  return { start, forget, busy, pending, session, state, status: status.data, unsafe, error: error ?? status.error }
}

export type DownloadFlow = ReturnType<typeof useDownloadFlow>
