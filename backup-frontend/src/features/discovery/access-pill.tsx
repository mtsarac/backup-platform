import { tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { TLS_UNAVAILABLE } from './status'
import type { DiscoveryInstanceSummary } from './types'

/** The access status of a server; a missing TLS says what is wrong instead of the generic "Hata". */
export function AccessPill({ status, reasonCode }: { status: DiscoveryInstanceSummary['access']; reasonCode: string | null }) {
  useLanguage()
  return reasonCode === TLS_UNAVAILABLE ? <StatusPill level="urgent">{tx("Şifreli bağlantı yok")}</StatusPill> : <StatusPill domain="access" status={status} />
}
