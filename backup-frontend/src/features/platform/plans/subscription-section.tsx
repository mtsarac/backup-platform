import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { linkClass } from '@/components/ui/form-styles'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { formatDateTime } from '@/lib/format'
import { AssignPlanDialog } from './assign-plan-dialog'
import { useResumeSubscription, useSuspendSubscription, useTenantSubscription } from './hooks'
import { OverrideDialog } from './override-dialog'
import { formatBytes, formatGb } from './storage'
import { subscriptionActions, type SubscriptionAction } from './subscription-actions'
import { SubscriptionHistory } from './subscription-history'
import { assignedByLabel, overrideLabel, sourceLabel } from './subscription-labels'
import type { Subscription } from './types'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/components/auth/access-denied'

const actionLabels: Record<SubscriptionAction, () => string> = {
  assign: () => tx('Plan ata'),
  change: () => tx('Planı değiştir'),
  suspend: () => tx('Askıya al'),
  resume: () => tx('Yeniden etkinleştir'),
  override: () => tx('Özel limit'),
}

function Current({ subscription }: { subscription: Subscription }) {
  useLanguage()
  const effective = subscription.effectiveStorageLimit
  const unknown = tx('Henüz bildirilmedi')
  return <div className="space-y-4">
    <Facts columns={3}>
      <Fact label="Plan"><span className="font-medium">{subscription.planName}</span></Fact>
      <Fact label={tx("Durum")}><StatusPill domain="subscription" status={subscription.status} /></Fact>
      <Fact label={tx("Etkin depolama limiti")}><span className="font-medium">{formatGb(effective.unlimited ? null : effective.bytes)}</span><div className="text-xs text-text-muted">{tx("Kaynak:")}{' '}{sourceLabel(effective.source)}</div></Fact>
    </Facts>
    {subscription.status === 'Expired' && <Notice tone="warning">{tx("Abonelik süresi doldu. Yeni bir plan atayabilirsiniz.")}</Notice>}
    <TechnicalDetails items={[
      ...(!effective.unlimited && effective.bytes !== null ? [{ label: tx("Etkin depolama limiti (bayt)"), value: formatBytes(effective.bytes) }] : []),
      ...(subscription.planStorageLimitBytes !== null ? [{ label: tx("Plan limiti (bayt)"), value: formatBytes(subscription.planStorageLimitBytes) }] : []),
      { label: tx("Override"), value: overrideLabel(subscription.storageLimitOverride) },
      { label: tx("Başlangıç"), value: formatDateTime(subscription.startsAt, unknown) },
      { label: tx("Bitiş"), value: subscription.endsAt ? formatDateTime(subscription.endsAt, unknown) : tx('Süresiz') },
      { label: tx("Atayan"), value: assignedByLabel(subscription) },
      { label: tx("Abonelik kimliği"), value: subscription.subscriptionId },
    ]} />
  </div>
}

export function SubscriptionSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const query = useTenantSubscription(tenantId)
  const suspend = useSuspendSubscription(tenantId)
  const resume = useResumeSubscription(tenantId)
  const [dialog, setDialog] = useState<SubscriptionAction | null>(null)
  const subscription = query.data?.subscription ?? null
  const { primary, others } = subscriptionActions(subscription)
  const close = () => setDialog(null)
  function open(action: SubscriptionAction) {
    suspend.reset()
    resume.reset()
    setDialog(action)
  }
  const button = (action: SubscriptionAction, variant: 'default' | 'outline') => <Button key={action} variant={variant} onClick={() => open(action)}>{actionLabels[action]()}</Button>
  return <div className="space-y-6">
    <Section title={tx("Plan ve kota")} description={tx("Çalışma alanının planı, aboneliği ve geçerli depolama limiti.")}
      actions={subscription && button(primary, 'default')}>
      <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
        {query.data && (subscription
          ? <div className="space-y-4">
            <Current subscription={subscription} />
            {others.length > 0 && <details>
              <summary className={`${linkClass} inline-flex min-h-11 cursor-pointer select-none items-center text-sm`}>{tx("Diğer işlemler")}</summary>
              <div className="flex flex-wrap gap-2 pt-2">{others.map((action) => button(action, 'outline'))}</div>
            </details>}
          </div>
          : <EmptyState message={tx("Bu çalışma alanı için henüz bir plan atanmadı.")} action={button('assign', 'default')} />)}
      </QueryState>
    </Section>
    {subscription && <SubscriptionHistory tenantId={tenantId} />}
    {dialog === 'assign' && <AssignPlanDialog tenantId={tenantId} replacing={null} onClose={close} />}
    {dialog === 'change' && subscription && <AssignPlanDialog tenantId={tenantId} replacing={subscription} onClose={close} />}
    {dialog === 'override' && subscription && <OverrideDialog tenantId={tenantId} tenantName={tenantName} subscription={subscription} onClose={close} />}
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
