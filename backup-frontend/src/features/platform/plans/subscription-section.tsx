import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { PlatformQueryState } from '../platform-query-state'
import { AssignPlanDialog } from './assign-plan-dialog'
import { useResumeSubscription, useSuspendSubscription, useTenantSubscription } from './hooks'
import { OverrideDialog } from './override-dialog'
import { StorageLimitText } from './storage-limit-text'
import { subscriptionActions } from './subscription-actions'
import { SubscriptionHistory } from './subscription-history'
import { assignedByLabel, overrideLabel, sourceLabel } from './subscription-labels'
import type { Subscription } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

type Dialog = 'assign' | 'suspend' | 'resume' | 'override' | null

function Current({ subscription }: { subscription: Subscription }) {
  useLanguage()
  const effective = subscription.effectiveStorageLimit
  const override = subscription.storageLimitOverride
  return <Facts columns={3}>
    <Fact label="Plan"><span className="font-medium">{subscription.planName}</span></Fact>
    <Fact label={tx("Durum")}><StatusPill domain="subscription" status={subscription.status} /></Fact>
    <Fact label={tx("Etkin depolama limiti")}><StorageLimitText bytes={effective.unlimited ? null : effective.bytes} /><div className="text-xs text-text-muted">{tx("Kaynak:")}{' '}{sourceLabel(effective.source)}</div></Fact>
    <Fact label={tx("Plan limiti")}><StorageLimitText bytes={subscription.planStorageLimitBytes} /></Fact>
    <Fact label={tx("Override")}>{override.kind === 'limited' && override.bytes !== null ? <StorageLimitText bytes={override.bytes} /> : overrideLabel(override)}</Fact>
    <Fact label={tx("Başlangıç")}>{formatDateTime(subscription.startsAt, tx('Henüz bildirilmedi'))}</Fact>
    <Fact label={tx("Bitiş")}>{subscription.endsAt ? formatDateTime(subscription.endsAt, tx('Henüz bildirilmedi')) : tx('Süresiz')}</Fact>
    <Fact label={tx("Atayan")}>{assignedByLabel(subscription)}</Fact>
  </Facts>
}

export function SubscriptionSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const query = useTenantSubscription(tenantId)
  const suspend = useSuspendSubscription(tenantId)
  const resume = useResumeSubscription(tenantId)
  const [dialog, setDialog] = useState<Dialog>(null)
  const subscription = query.data?.subscription ?? null
  const actions = subscriptionActions(subscription)
  const close = () => setDialog(null)
  return <div className="space-y-6">
    <Section title={tx("Abonelik")} description={tx("Plan, etkin depolama limiti ve limit override.")}
      actions={query.data && <>
        {actions.assign && <Button variant={subscription ? 'outline' : 'default'} onClick={() => setDialog('assign')}>{actions.assign === 'change' ? tx('Planı değiştir') : tx('Plan ata')}</Button>}
        {actions.suspend && <Button variant="outline" onClick={() => { suspend.reset(); setDialog('suspend') }}>{tx("Askıya al")}</Button>}
        {actions.resume && <Button variant="outline" onClick={() => { resume.reset(); setDialog('resume') }}>{tx("Yeniden etkinleştir")}</Button>}
        {actions.override && <Button variant="outline" onClick={() => setDialog('override')}>{tx("Limit override")}</Button>}
      </>}>
      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {query.data && (subscription
          ? <div className="space-y-4">
            <Current subscription={subscription} />
            {subscription.status === 'Expired' && <Notice tone="warning">{tx("Abonelik süresi doldu. Yeni bir plan atayabilirsiniz.")}</Notice>}
          </div>
          : <EmptyState message={tx("Bu tenant için henüz bir plan atanmadı.")} />)}
      </PlatformQueryState>
    </Section>
    {subscription && <SubscriptionHistory tenantId={tenantId} />}
    {dialog === 'assign' && <AssignPlanDialog tenantId={tenantId} current={subscription} onClose={close} />}
    {dialog === 'override' && subscription && <OverrideDialog tenantId={tenantId} subscription={subscription} onClose={close} />}
    <ConfirmDialog open={dialog === 'suspend'} onClose={close} title={tx("Abonelik askıya alınsın mı?")} confirmLabel={tx("Askıya al")}
      consequence={{
        what: tx("“{0}” çalışma alanının aboneliği askıya alınır ve depolama limiti hemen değişir.", { "0": tenantName }),
        who: tx("Bu çalışma alanının tüm kullanıcıları ve yedekleme işleri etkilenir."),
        reversible: tx("Evet. Aboneliği yeniden etkinleştirebilirsiniz."),
      }}
      pending={suspend.isPending} error={suspend.error}
      onConfirm={() => suspend.mutate(undefined, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'resume'} onClose={close} title={tx("Abonelik yeniden etkinleştirilsin mi?")} confirmLabel={tx("Yeniden etkinleştir")}
      consequence={{
        what: tx("“{0}” çalışma alanının askıdaki aboneliği yeniden etkin olur ve depolama limiti hemen geri gelir.", { "0": tenantName }),
        who: tx("Bu çalışma alanının tüm kullanıcıları ve yedekleme işleri etkilenir."),
        reversible: tx("Evet. Aboneliği yeniden askıya alabilirsiniz."),
      }}
      pending={resume.isPending} error={resume.error}
      onConfirm={() => resume.mutate(undefined, { onSuccess: close })} />
  </div>
}
