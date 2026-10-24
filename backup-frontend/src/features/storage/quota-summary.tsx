import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { allocatedPercent } from './format'
import { isLegacyUnlimited } from './labels'
import { QuotaMeter } from './quota-meter'
import type { TenantQuota } from './types'
import { formatBytes, formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { ErrorNotice, Notice } from '@/components/ui/notice'

type Standing = Pick<TenantQuota, 'limitBytes' | 'unlimited' | 'usedBytes' | 'reservedBytes' | 'remainingBytes' | 'state' | 'subscriptionStatus' | 'plan' | 'calculatedAt'>

export function Fact({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  useLanguage()
  return <div><dt className="text-xs text-text-muted">{tx(label)}</dt><dd className="mt-0.5 text-sm">{children}</dd>{hint && <p className="mt-0.5 text-xs text-text-muted">{hint}</p>}</div>
}

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

/** Used and Reserved side by side, the committed total against the limit, the meter and what is left. */
export function QuotaSummary({ quota }: { quota: Standing }) {
  useLanguage()
  const committed = quota.usedBytes + quota.reservedBytes
  const percent = allocatedPercent(quota.usedBytes, quota.reservedBytes, quota.limitBytes)
  const noLimit = quota.limitBytes === null
  return <div className="space-y-4">
    <dl className="grid gap-4 sm:grid-cols-3">
      <Fact label={tx("Kullanılan (Used)")} hint={tx("Saklanan backup'ların kapasiteye sayılan boyutu.")}><Capacity bytes={quota.usedBytes} /></Fact>
      <Fact label={tx("Rezerve (Reserved)")} hint={tx("Çalışan veya sıradaki backup'lar için ayrılan tahmini alan.")}><Capacity bytes={quota.reservedBytes} /></Fact>
      <Fact label={tx("Toplam")} hint={percent === null ? undefined : tx("%{0} ayrılmış", { "0": percent })}>
        <span className="text-lg font-semibold text-text-heading">{noLimit ? formatBytes(committed) : `${formatBytes(committed)} / ${formatBytes(quota.limitBytes ?? 0)}`}</span>
      </Fact>
    </dl>
    <QuotaMeter usedBytes={quota.usedBytes} reservedBytes={quota.reservedBytes} limitBytes={quota.limitBytes} state={quota.state} />
    <dl className="grid gap-4 sm:grid-cols-3">
      <Fact label={tx("Etkin limit")}>{noLimit
        ? (quota.subscriptionStatus === 'NoSubscription' ? tx('Tanımlı değil (abonelik yok)') : isLegacyUnlimited(quota.plan) ? tx('Limitsiz (Legacy Unlimited)') : 'Limitsiz')
        : <Capacity bytes={quota.limitBytes ?? 0} />}</Fact>
      <Fact label={tx("Kalan")}>{quota.remainingBytes === null ? '—' : <Capacity bytes={quota.remainingBytes} />}</Fact>
      <Fact label={tx("Son hesaplama")}>{formatDateTime(quota.calculatedAt, tx('Henüz bildirilmedi'))}</Fact>
    </dl>
  </div>
}

const dataSafe = 'Mevcut backup\'larınız silinmez ve korunur; görüntüleme, restore, indirme ve silme işlemleri etkilenmez.'

const subscriptionExplanations: Record<'Suspended' | 'Expired' | 'NoSubscription', { title: string; text: string }> = {
  Suspended: { get title() { return tx("Abonelik askıda") }, get text() { return tx("Aboneliğiniz askıya alındığı için yeni backup başlatılamıyor.") } },
  Expired: { get title() { return tx("Abonelik süresi doldu") }, get text() { return tx("Aboneliğinizin süresi dolduğu için yeni backup başlatılamıyor.") } },
  NoSubscription: { get title() { return tx("Abonelik yok") }, get text() { return tx("Bu tenant için bir plan atanmadığından yeni backup başlatılamıyor.") } },
}

/**
 * What the tenant can and cannot do right now, and why. A subscription restriction (`subscription_required`) and a full quota
 * (`quota_exceeded`) are different problems with different remedies, so they never share a message.
 */
export function StorageNotices({ quota }: { quota: Pick<TenantQuota, 'subscriptionStatus' | 'state' | 'newBackupsAllowed' | 'enforcementMode' | 'warningThreshold' | 'criticalThreshold' | 'plan'> }) {
  useLanguage()
  const status = quota.subscriptionStatus
  // Explanation only: with an open subscription the backend's own admission answer says whether the quota blocks; otherwise that answer is the subscription's, so the policy mode is read.
  const quotaBlocks = status === 'Active' ? !quota.newBackupsAllowed : quota.enforcementMode === 'BlockNewBackups'
  return <>
    {status !== 'Active' && <Notice tone="warning" title={subscriptionExplanations[status].title}>
      <p>{subscriptionExplanations[status].text}{' '}{tx("Bu bir kota sorunu değildir; yeni backup için platform yöneticinizin bir plan atamasını veya aboneliği yeniden etkinleştirmesini isteyin.")}</p>
      <p>{tx(dataSafe)}</p>
    </Notice>}
    {quota.state === 'OverQuota' && status !== 'NoSubscription' && (!quotaBlocks
      ? <Notice tone="warning" title={tx("Depolama limiti aşıldı")}>
        <p>{tx("Kullanım limite ulaştı. Bu tenant'ın kota politikası yalnızca uyarır; yeni backup'lar kota nedeniyle engellenmiyor.")}</p>
        <p>{tx(dataSafe)}</p>
      </Notice>
      : <ErrorNotice title={tx("Depolama kotası doldu (OverQuota)")} what={tx("Kullanılan ve rezerve edilen alan limite ulaştığı için yeni backup'lar başlatılamıyor (quota_exceeded).")} dataImpact={tx(dataSafe)}
        action={tx("Yer açmak için eski backup'ları silebilir ya da daha büyük bir plan için platform yöneticinizle iletişime geçebilirsiniz.")} />)}
    {(quota.state === 'Warning' || quota.state === 'Critical') && <Notice tone="info" title={quota.state === 'Critical' ? tx('Kapasite kritik seviyede') : tx('Kapasite dolmaya yaklaşıyor')}>
      <p>{tx("Politika eşikleri: Warning %")}{quota.warningThreshold}{tx(", Critical %")}{quota.criticalThreshold}{tx(". Limite ulaşıldığında yeni backup'lar engellenebilir; mevcut backup'lar korunur.")}</p>
    </Notice>}
    {isLegacyUnlimited(quota.plan) && <Notice tone="info" title={tx("Legacy Unlimited")}>
      <p>{tx("Bu tenant kota öncesi sınırsız plandadır; depolama limiti yoktur. Kullanım yine de izlenir.")}</p>
    </Notice>}
  </>
}
