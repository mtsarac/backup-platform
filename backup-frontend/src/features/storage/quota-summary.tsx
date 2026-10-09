import type { ReactNode } from 'react'
import { Info, ShieldAlert } from 'lucide-react'
import { agentDate } from '@/features/agents/format'
import { allocatedPercent, formatCapacity, formatExactBytes } from './format'
import { isLegacyUnlimited } from './labels'
import { EntitlementStatusBadge, QuotaStateBadge } from './quota-badges'
import { QuotaMeter } from './quota-meter'
import type { TenantQuota } from './types'

type Standing = Pick<TenantQuota, 'limitBytes' | 'unlimited' | 'usedBytes' | 'reservedBytes' | 'remainingBytes' | 'state' | 'subscriptionStatus' | 'plan' | 'calculatedAt'>

export function Fact({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return <div><dt className="text-xs text-text-muted">{label}</dt><dd className="mt-0.5 text-sm">{children}</dd>{hint && <p className="mt-0.5 text-xs text-text-muted">{hint}</p>}</div>
}

function Capacity({ bytes }: { bytes: number }) {
  return <span className="inline-flex flex-col"><span className="text-lg font-semibold text-text-heading">{formatCapacity(bytes)}</span><span className="text-xs text-text-muted">{formatExactBytes(bytes)}</span></span>
}

/**
 * A tenant with no subscription has no limit, which the backend reports as `Unlimited`. That is not an entitlement to unlimited storage: it
 * cannot start backups at all. So the badge shows the subscription standing instead of a state that would read like a plan.
 */
export function StandingBadge({ quota }: { quota: Pick<TenantQuota, 'state' | 'subscriptionStatus'> }) {
  return quota.subscriptionStatus === 'NoSubscription' ? <EntitlementStatusBadge status="NoSubscription" /> : <QuotaStateBadge state={quota.state} />
}

/** Used and Reserved side by side, the committed total against the limit, the meter and what is left. */
export function QuotaSummary({ quota }: { quota: Standing }) {
  const committed = quota.usedBytes + quota.reservedBytes
  const percent = allocatedPercent(quota.usedBytes, quota.reservedBytes, quota.limitBytes)
  const noLimit = quota.limitBytes === null
  return <div className="space-y-4">
    <dl className="grid gap-4 sm:grid-cols-3">
      <Fact label="Kullanılan (Used)" hint="Saklanan backup'ların kapasiteye sayılan boyutu."><Capacity bytes={quota.usedBytes} /></Fact>
      <Fact label="Rezerve (Reserved)" hint="Çalışan veya sıradaki backup'lar için ayrılan tahmini alan."><Capacity bytes={quota.reservedBytes} /></Fact>
      <Fact label="Toplam" hint={percent === null ? undefined : `%${percent} ayrılmış`}>
        <span className="text-lg font-semibold text-text-heading">{noLimit ? formatCapacity(committed) : `${formatCapacity(committed)} / ${formatCapacity(quota.limitBytes ?? 0)}`}</span>
      </Fact>
    </dl>
    <QuotaMeter usedBytes={quota.usedBytes} reservedBytes={quota.reservedBytes} limitBytes={quota.limitBytes} state={quota.state} />
    <dl className="grid gap-4 sm:grid-cols-3">
      <Fact label="Etkin limit">{noLimit
        ? (quota.subscriptionStatus === 'NoSubscription' ? 'Tanımlı değil (abonelik yok)' : isLegacyUnlimited(quota.plan) ? 'Limitsiz (Legacy Unlimited)' : 'Limitsiz')
        : <Capacity bytes={quota.limitBytes ?? 0} />}</Fact>
      <Fact label="Kalan">{quota.remainingBytes === null ? '—' : <Capacity bytes={quota.remainingBytes} />}</Fact>
      <Fact label="Son hesaplama">{agentDate(quota.calculatedAt)}</Fact>
    </dl>
  </div>
}

function Notice({ tone, title, children }: { tone: 'error' | 'warning' | 'info'; title: string; children: ReactNode }) {
  const classes = tone === 'error' ? 'border-error/30 bg-error-subtle text-error' : tone === 'warning' ? 'border-warning/30 bg-warning-subtle text-warning' : 'border-info/30 bg-info-subtle text-info'
  const Icon = tone === 'info' ? Info : ShieldAlert
  return <div role={tone === 'info' ? 'status' : 'alert'} className={`space-y-1 rounded-lg border p-4 ${classes}`}>
    <div className="flex items-center gap-2"><Icon className="size-4 shrink-0" aria-hidden="true" /><h2 className="font-medium">{title}</h2></div>
    <div className="space-y-1 text-sm">{children}</div>
  </div>
}

const dataSafe = 'Mevcut backup\'larınız silinmez ve korunur; görüntüleme, restore, indirme ve silme işlemleri etkilenmez.'

const subscriptionExplanations: Record<'Suspended' | 'Expired' | 'NoSubscription', { title: string; text: string }> = {
  Suspended: { title: 'Abonelik askıda', text: 'Aboneliğiniz askıya alındığı için yeni backup başlatılamıyor.' },
  Expired: { title: 'Abonelik süresi doldu', text: 'Aboneliğinizin süresi dolduğu için yeni backup başlatılamıyor.' },
  NoSubscription: { title: 'Abonelik yok', text: 'Bu tenant için bir plan atanmadığından yeni backup başlatılamıyor.' },
}

/**
 * What the tenant can and cannot do right now, and why. A subscription restriction (`subscription_required`) and a full quota
 * (`quota_exceeded`) are different problems with different remedies, so they never share a message.
 */
export function StorageNotices({ quota }: { quota: Pick<TenantQuota, 'subscriptionStatus' | 'state' | 'newBackupsAllowed' | 'enforcementMode' | 'warningThreshold' | 'criticalThreshold' | 'plan'> }) {
  const status = quota.subscriptionStatus
  // Explanation only: with an open subscription the backend's own admission answer says whether the quota blocks; otherwise that answer is the subscription's, so the policy mode is read.
  const quotaBlocks = status === 'Active' ? !quota.newBackupsAllowed : quota.enforcementMode === 'BlockNewBackups'
  return <>
    {status !== 'Active' && <Notice tone="warning" title={subscriptionExplanations[status].title}>
      <p>{subscriptionExplanations[status].text} Bu bir kota sorunu değildir; yeni backup için platform yöneticinizin bir plan atamasını veya aboneliği yeniden etkinleştirmesini isteyin.</p>
      <p>{dataSafe}</p>
    </Notice>}
    {quota.state === 'OverQuota' && status !== 'NoSubscription' && (!quotaBlocks
      ? <Notice tone="warning" title="Depolama limiti aşıldı">
        <p>Kullanım limite ulaştı. Bu tenant'ın kota politikası yalnızca uyarır; yeni backup'lar kota nedeniyle engellenmiyor.</p>
        <p>{dataSafe}</p>
      </Notice>
      : <Notice tone="error" title="Depolama kotası doldu (OverQuota)">
        <p>Kullanılan ve rezerve edilen alan limite ulaştığı için yeni backup'lar başlatılamıyor (quota_exceeded).</p>
        <p>{dataSafe}</p>
        <p>Yer açmak için eski backup'ları silebilir ya da daha büyük bir plan için platform yöneticinizle iletişime geçebilirsiniz.</p>
      </Notice>)}
    {(quota.state === 'Warning' || quota.state === 'Critical') && <Notice tone="info" title={quota.state === 'Critical' ? 'Kapasite kritik seviyede' : 'Kapasite dolmaya yaklaşıyor'}>
      <p>Politika eşikleri: Warning %{quota.warningThreshold}, Critical %{quota.criticalThreshold}. Limite ulaşıldığında yeni backup'lar engellenebilir; mevcut backup'lar korunur.</p>
    </Notice>}
    {isLegacyUnlimited(quota.plan) && <Notice tone="info" title="Legacy Unlimited">
      <p>Bu tenant kota öncesi sınırsız plandadır; depolama limiti yoktur. Kullanım yine de izlenir.</p>
    </Notice>}
  </>
}
