import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { agentDate } from '@/features/agents/format'
import { platformCrumb } from '@/features/platform/crumbs'
import { bareTableClass, ListEmpty } from '@/features/platform/list-empty'
import { accountingModeLabels, enforcementModeLabels, scopeLabels } from '@/features/storage/labels'
import type { QuotaPolicy } from '@/features/storage/types'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { useQuotaPolicies } from '@/features/platform/storage/hooks'
import { QuotaPolicyDialog } from '@/features/platform/storage/quota-policy-dialog'

export function PlatformQuotaPoliciesPage() {
  const query = useQuotaPolicies()
  const [editingPolicy, setEditingPolicy] = useState<QuotaPolicy | null>(null)
  const [isCreating, setIsCreating] = useState(false)
  const policies = query.data?.items ?? []
  const columns: Column<QuotaPolicy>[] = [
    { key: 'name', header: 'Politika', wrap: true, render: (policy) => <div className="space-y-1"><span className="font-semibold">{policy.name}</span><div className="flex flex-wrap gap-1.5">{policy.isDefault && <Badge tone="info">Varsayılan</Badge>}<Badge tone={policy.enabled ? 'success' : 'neutral'} dot>{policy.enabled ? 'Etkin' : 'Devre Dışı'}</Badge></div><p className="text-sm text-text-muted">Anahtar: <code>{policy.key}</code> · Sürüm: v{policy.version}</p></div> },
    { key: 'accounting', header: 'Hesaplama modu', wrap: true, render: (policy) => accountingModeLabels[policy.accountingMode] },
    { key: 'enforcement', header: 'Uygulama modu', wrap: true, render: (policy) => enforcementModeLabels[policy.enforcementMode] },
    { key: 'thresholds', header: 'Eşikler', render: (policy) => <div className="text-sm tabular-nums"><div>Warning: %{policy.warningThresholdPercent}</div><div>Critical: %{policy.criticalThresholdPercent}</div></div> },
    { key: 'scope', header: 'Kapsam', wrap: true, render: (policy) => <span className="text-sm text-text-muted">{scopeLabels(policy.includedStorageScopes).join('; ')}</span> },
    { key: 'updatedAt', header: 'Güncellendi', render: (policy) => agentDate(policy.updatedAt) },
    { key: 'actions', header: 'İşlemler', render: (policy) => <Button variant="outline" size="sm" onClick={() => setEditingPolicy(policy)}>Düzenle</Button> },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title="Kota Politikaları" description="Tenant'ların depolama hesaplama yöntemlerini, eşiklerini ve engelleme kurallarını yönetin." actions={<>
      <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw aria-hidden="true" className={`size-4 ${query.isFetching ? 'animate-spin motion-reduce:animate-none' : ''}`} />Yenile</Button>
      <Button onClick={() => setIsCreating(true)}><Plus aria-hidden="true" className="size-4" />Yeni Politika</Button>
    </>} />
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && (policies.length === 0
        ? <ListEmpty title="Tanımlı kota politikası bulunamadı." action={<Button onClick={() => setIsCreating(true)}>Yeni Politika</Button>}>İlk politikayı oluşturarak hesaplama kapsamını ve eşikleri tanımlayın.</ListEmpty>
        : <Section title="Kota politikaları" description={`${policies.length} politika · Değişiklikler mevcut kullanım hesaplamalarını etkileyebilir.`} flush className="overflow-hidden"><DataTable className={bareTableClass} rows={policies} columns={columns} keyExtractor={(policy) => policy.policyId} /></Section>)}
    </PlatformQueryState>
    {isCreating && <QuotaPolicyDialog onClose={() => setIsCreating(false)} />}
    {editingPolicy && <QuotaPolicyDialog policy={editingPolicy} onClose={() => setEditingPolicy(null)} />}
  </div>
}
