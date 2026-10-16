import { tx } from '@/lib/i18n'
import { useEffect, useState } from 'react'

/** dataUpdatedAt is the client receipt time, not the browser's possibly skewed wall clock at render. */
export function useSupportCountdown(expiresAt: string, serverNow: string, receivedAt: number) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  const adjustedNow = Date.parse(serverNow) + Math.max(0, now - receivedAt)
  const seconds = Math.max(0, Math.ceil((Date.parse(expiresAt) - adjustedNow) / 1000))
  return { seconds, label: tx("{0} sa {1} dk {2} sn", { "0": Math.floor(seconds / 3600), "1": Math.floor((seconds % 3600) / 60), "2": seconds % 60 }) }
}
