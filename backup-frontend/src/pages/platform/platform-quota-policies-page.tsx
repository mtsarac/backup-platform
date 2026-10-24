import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { platformCrumb } from '@/features/platform/crumbs'
import { bareListClass, ListEmpty } from '@/features/platform/list-empty'
import { accountingModeLabels, enforcementModeLabels, scopeLabels } from '@/features/storage/labels'
import type { QuotaPolicy } from '@/features/storage/types'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { useQuotaPolicies } from '@/features/platform/storage/hooks'
import { QuotaPolicyDialog } from '@/features/platform/storage/quota-policy-dialog'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

export function PlatformQuotaPoliciesPage() {
  useLanguage()
  const query = useQuotaPolicies()
  const [editingPolicy, setEditingPolicy] = useState<QuotaPolicy | null>(null)
  const [isCreating, setIsCreating] = useState(false)
  const policies = query.data?.items ?? []
  const columns: Column<QuotaPolicy>[] = [
    { key: 'name', header: tx('Politika'), wrap: true, render: (policy) => <div className="space-y-1"><span className="font-semibold">{policy.name}</span><div className="flex flex-wrap gap-1.5">{policy.isDefault && <Badge tone="info">{tx("Varsayılan")}</Badge>}<StatusPill level={policy.enabled ? 'safe' : 'neutral'}>{policy.enabled ? tx('Etkin') : tx('Devre Dışı')}</StatusPill></div><p className="text-sm text-text-muted">{tx("Anahtar:")}{' '}<code>{policy.key}</code>{' '}{tx("· Sürüm: v")}{policy.version}</p></div> },
    { key: 'accounting', header: tx('Hesaplama modu'), wrap: true, render: (policy) => accountingModeLabels[policy.accountingMode] },
    { key: 'enforcement', header: tx('Uygulama modu'), wrap: true, render: (policy) => enforcementModeLabels[policy.enforcementMode] },
    { key: 'thresholds', header: tx('Eşikler'), render: (policy) => <div className="text-sm tabular-nums"><div>{tx("Warning: %")}{policy.warningThresholdPercent}</div><div>{tx("Critical: %")}{policy.criticalThresholdPercent}</div></div> },
    { key: 'scope', card: 'hidden', header: tx('Kapsam'), wrap: true, render: (policy) => <span className="text-sm text-text-muted">{scopeLabels(policy.includedStorageScopes).join('; ')}</span> },
    { key: 'updatedAt', card: 'hidden', header: tx('Güncellendi'), render: (policy) => formatDateTime(policy.updatedAt, tx('Henüz bildirilmedi')) },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: (policy) => <Button variant="outline" size="sm" onClick={() => setEditingPolicy(policy)}>{tx("Düzenle")}</Button> },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Kota Politikaları")} description={tx("Tenant'ların depolama hesaplama yöntemlerini, eşiklerini ve engelleme kurallarını yönetin.")} actions={<>
      <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw aria-hidden="true" className={`size-4 ${query.isFetching ? 'animate-spin motion-reduce:animate-none' : ''}`} />{tx("Yenile")}</Button>
      <Button onClick={() => setIsCreating(true)}><Plus aria-hidden="true" className="size-4" />{tx("Yeni Politika")}</Button>
    </>} />
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && (policies.length === 0
        ? <ListEmpty message={tx("Tanımlı kota politikası bulunamadı.")} action={<Button onClick={() => setIsCreating(true)}>{tx("Yeni Politika")}</Button>} />
        : <Section title={tx("Kota politikaları")} description={tx("{0} politika · Değişiklikler mevcut kullanım hesaplamalarını etkileyebilir.", { "0": policies.length })} flush className="overflow-hidden"><DataList className={bareListClass} rows={policies} columns={columns} keyExtractor={(policy) => policy.policyId} /></Section>)}
    </PlatformQueryState>
    {isCreating && <QuotaPolicyDialog onClose={() => setIsCreating(false)} />}
    {editingPolicy && <QuotaPolicyDialog policy={editingPolicy} onClose={() => setEditingPolicy(null)} />}
  </div>
}
