import { tx, useLanguage } from '@/lib/i18n'
import { Fact, Facts } from '@/components/ui/section'
import { allocatedPercent } from './format'
import { QuotaMeter } from './quota-meter'
import type { TenantQuota } from './types'
import { formatBytes, formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

type Standing = Pick<TenantQuota, 'limitBytes' | 'unlimited' | 'usedBytes' | 'reservedBytes' | 'remainingBytes' | 'state' | 'subscriptionStatus' | 'plan' | 'calculatedAt'>


function Capacity({ bytes }: { bytes: number }) {
  useLanguage()
  return <span className="text-lg font-semibold text-text-heading">{formatBytes(bytes)}</span>
}

/**
 * A tenant with no subscription has no limit, which the backend reports as `Unlimited`. That is not an entitlement to unlimited storage: it
 * cannot start backups at all. So the badge shows the subscription standing instead of a state that would read like a plan.
 */
export function StandingBadge({ quota }: { quota: Pick<TenantQuota, 'state' | 'subscriptionStatus'> }) {
  useLanguage()
  return quota.subscriptionStatus === 'NoSubscription' ? <StatusPill domain="subscription" status="NoSubscription" /> : <StatusPill domain="quota" status={quota.state} />
}

/** Used and Reserved side by side with the committed total, the meter against the limit, and what is left. The limit is stated once, in the second row. */
export function QuotaSummary({ quota }: { quota: Standing }) {
  useLanguage()
  const committed = quota.usedBytes + quota.reservedBytes
  const percent = allocatedPercent(quota.usedBytes, quota.reservedBytes, quota.limitBytes)
  const noLimit = quota.limitBytes === null
  return <div className="space-y-4">
    <Facts columns={3}>
      <Fact label={tx("Kullanılan (Used)")} hint={tx("Saklanan backup'ların kapasiteye sayılan boyutu.")}><Capacity bytes={quota.usedBytes} /></Fact>
      <Fact label={tx("Rezerve (Reserved)")} hint={tx("Çalışan veya sıradaki backup'lar için ayrılan tahmini alan.")}><Capacity bytes={quota.reservedBytes} /></Fact>
      <Fact label={tx("Toplam")} hint={percent === null ? undefined : tx("%{0} ayrılmış", { "0": percent })}><Capacity bytes={committed} /></Fact>
    </Facts>
    <QuotaMeter usedBytes={quota.usedBytes} reservedBytes={quota.reservedBytes} limitBytes={quota.limitBytes} state={quota.state} />
    <Facts columns={3}>
      <Fact label={tx("Etkin limit")}>{noLimit
        ? (quota.subscriptionStatus === 'NoSubscription' ? tx('Tanımlı değil (abonelik yok)') : tx('Limitsiz'))
        : <Capacity bytes={quota.limitBytes ?? 0} />}</Fact>
      <Fact label={tx("Kalan")}>{quota.remainingBytes === null ? '—' : <Capacity bytes={quota.remainingBytes} />}</Fact>
      <Fact label={tx("Son hesaplama")}>{formatDateTime(quota.calculatedAt, tx('Henüz bildirilmedi'))}</Fact>
    </Facts>
  </div>
}
