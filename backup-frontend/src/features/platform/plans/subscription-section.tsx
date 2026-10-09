import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
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

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return <div><dt className="text-xs text-text-muted">{label}</dt><dd className="mt-0.5 text-sm">{children}</dd></div>
}

function Current({ subscription }: { subscription: Subscription }) {
  const effective = subscription.effectiveStorageLimit
  const override = subscription.storageLimitOverride
  return <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
    <Fact label="Plan"><span className="font-medium">{subscription.planName}</span></Fact>
    <Fact label="Durum"><SubscriptionStatusPill status={subscription.status} /></Fact>
    <Fact label="Etkin depolama limiti"><StorageLimitText bytes={effective.unlimited ? null : effective.bytes} /><div className="text-xs text-text-muted">Kaynak: {sourceLabel(effective.source)}</div></Fact>
    <Fact label="Plan limiti"><StorageLimitText bytes={subscription.planStorageLimitBytes} /></Fact>
    <Fact label="Override">{override.kind === 'limited' && override.bytes !== null ? <StorageLimitText bytes={override.bytes} /> : overrideLabel(override)}</Fact>
    <Fact label="Başlangıç">{agentDate(subscription.startsAt)}</Fact>
    <Fact label="Bitiş">{subscription.endsAt ? agentDate(subscription.endsAt) : 'Süresiz'}</Fact>
    <Fact label="Atayan">{assignedByLabel(subscription)}</Fact>
  </dl>
}

export function SubscriptionSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  const query = useTenantSubscription(tenantId)
  const suspend = useSuspendSubscription(tenantId)
  const resume = useResumeSubscription(tenantId)
  const [dialog, setDialog] = useState<Dialog>(null)
  const subscription = query.data?.subscription ?? null
  const actions = subscriptionActions(subscription)
  const close = () => setDialog(null)
  return <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="subscription-heading">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h2 id="subscription-heading" className="font-medium text-text-heading">Abonelik</h2>
      {query.data && <div className="flex flex-wrap gap-2">
        {actions.assign && <Button variant={subscription ? 'outline' : 'default'} onClick={() => setDialog('assign')}>{actions.assign === 'change' ? 'Planı değiştir' : 'Plan ata'}</Button>}
        {actions.suspend && <Button variant="outline" onClick={() => { suspend.reset(); setDialog('suspend') }}>Askıya al</Button>}
        {actions.resume && <Button variant="outline" onClick={() => { resume.reset(); setDialog('resume') }}>Yeniden etkinleştir</Button>}
        {actions.override && <Button variant="outline" onClick={() => setDialog('override')}>Limit override</Button>}
      </div>}
    </div>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && (subscription
        ? <>
          <Current subscription={subscription} />
          {subscription.status === 'Expired' && <p className="text-sm text-text-muted">Abonelik süresi doldu. Yeni bir plan atayabilirsiniz.</p>}
          <SubscriptionHistory tenantId={tenantId} />
        </>
        : <div className="space-y-1 rounded-lg border border-dashed border-border p-4"><p className="font-medium text-text-heading">Abonelik yok</p><p className="text-sm text-text-muted">Bu tenant için henüz bir plan atanmadı.</p></div>)}
    </PlatformQueryState>
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
  </section>
}
