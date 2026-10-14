import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { errorMessage } from '@/lib/api/failure-messages'
import { PlatformQueryState } from '../platform-query-state'
import { AssignPlanDialog } from './assign-plan-dialog'
import { useResumeSubscription, useSuspendSubscription, useTenantSubscription } from './hooks'
import { OverrideDialog } from './override-dialog'
import { StorageLimitText } from './storage-limit-text'
import { subscriptionActions } from './subscription-actions'
import { SubscriptionHistory } from './subscription-history'
import { assignedByLabel, overrideLabel, sourceLabel } from './subscription-labels'
import { SubscriptionStatusPill } from './subscription-presentation'
import type { Subscription } from './types'

type Dialog = 'assign' | 'suspend' | 'resume' | 'override' | null

function Current({ subscription }: { subscription: Subscription }) {
  const effective = subscription.effectiveStorageLimit
  const override = subscription.storageLimitOverride
  return <Facts columns={3}>
    <Fact label="Plan"><span className="font-medium">{subscription.planName}</span></Fact>
    <Fact label="Durum"><SubscriptionStatusPill status={subscription.status} /></Fact>
    <Fact label="Etkin depolama limiti"><StorageLimitText bytes={effective.unlimited ? null : effective.bytes} /><div className="text-xs text-text-muted">Kaynak: {sourceLabel(effective.source)}</div></Fact>
    <Fact label="Plan limiti"><StorageLimitText bytes={subscription.planStorageLimitBytes} /></Fact>
    <Fact label="Override">{override.kind === 'limited' && override.bytes !== null ? <StorageLimitText bytes={override.bytes} /> : overrideLabel(override)}</Fact>
    <Fact label="Başlangıç">{agentDate(subscription.startsAt)}</Fact>
    <Fact label="Bitiş">{subscription.endsAt ? agentDate(subscription.endsAt) : 'Süresiz'}</Fact>
    <Fact label="Atayan">{assignedByLabel(subscription)}</Fact>
  </Facts>
}

export function SubscriptionSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  const query = useTenantSubscription(tenantId)
  const suspend = useSuspendSubscription(tenantId)
  const resume = useResumeSubscription(tenantId)
  const [dialog, setDialog] = useState<Dialog>(null)
  const subscription = query.data?.subscription ?? null
  const actions = subscriptionActions(subscription)
  const close = () => setDialog(null)
  return <div className="space-y-6">
    <Section title="Abonelik" description="Plan, etkin depolama limiti ve limit override."
      actions={query.data && <>
        {actions.assign && <Button variant={subscription ? 'outline' : 'default'} onClick={() => setDialog('assign')}>{actions.assign === 'change' ? 'Planı değiştir' : 'Plan ata'}</Button>}
        {actions.suspend && <Button variant="outline" onClick={() => { suspend.reset(); setDialog('suspend') }}>Askıya al</Button>}
        {actions.resume && <Button variant="outline" onClick={() => { resume.reset(); setDialog('resume') }}>Yeniden etkinleştir</Button>}
        {actions.override && <Button variant="outline" onClick={() => setDialog('override')}>Limit override</Button>}
      </>}>
      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {query.data && (subscription
          ? <div className="space-y-4">
            <Current subscription={subscription} />
            {subscription.status === 'Expired' && <Notice tone="warning">Abonelik süresi doldu. Yeni bir plan atayabilirsiniz.</Notice>}
          </div>
          : <EmptyState title="Abonelik yok">Bu tenant için henüz bir plan atanmadı.</EmptyState>)}
      </PlatformQueryState>
    </Section>
    {subscription && <SubscriptionHistory tenantId={tenantId} />}
    {dialog === 'assign' && <AssignPlanDialog tenantId={tenantId} current={subscription} onClose={close} />}
    {dialog === 'override' && subscription && <OverrideDialog tenantId={tenantId} subscription={subscription} onClose={close} />}
    <ConfirmDialog open={dialog === 'suspend'} onClose={close} title="Abonelik askıya alınsın mı?" confirmLabel="Askıya al"
      description={`“${tenantName}” tenant'ının aboneliği askıya alınacak; daha sonra yeniden etkinleştirilebilir.`}
      pending={suspend.isPending} error={suspend.error ? errorMessage(suspend.error) : undefined}
      onConfirm={() => suspend.mutate(undefined, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'resume'} onClose={close} title="Abonelik yeniden etkinleştirilsin mi?" confirmLabel="Yeniden etkinleştir"
      description={`“${tenantName}” tenant'ının askıdaki aboneliği yeniden etkin olacak.`}
      pending={resume.isPending} error={resume.error ? errorMessage(resume.error) : undefined}
      onConfirm={() => resume.mutate(undefined, { onSuccess: close })} />
  </div>
}
