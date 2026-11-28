import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { platformCrumb } from '@/features/platform/crumbs'
import { bareListClass, ListEmpty } from '@/features/lists/list-empty'
import { compareDate, compareNumber, compareText } from '@/features/lists/compare'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { useClientList } from '@/features/lists/use-client-list'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { accountingModeLabels, enforcementModeLabels, scopeLabels } from '@/features/storage/labels'
import type { QuotaPolicy } from '@/features/storage/types'
import { useQuotaPolicies } from '@/features/platform/storage/hooks'
import { QuotaPolicyDialog } from '@/features/platform/storage/quota-policy-dialog'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

const listConfig = {
  search: true,
  sort: { first: { name: 'asc', status: 'asc', enforcement: 'asc', created: 'desc' }, default: { key: 'created', dir: 'desc' } },
} as const
const comparators = {
  name: (a: QuotaPolicy, b: QuotaPolicy) => compareText(a.name, b.name),
  status: (a: QuotaPolicy, b: QuotaPolicy) => compareNumber(Number(b.enabled), Number(a.enabled)),
  enforcement: (a: QuotaPolicy, b: QuotaPolicy) => compareText(enforcementModeLabels[a.enforcementMode], enforcementModeLabels[b.enforcementMode]),
  created: (a: QuotaPolicy, b: QuotaPolicy) => compareDate(a.createdAt, b.createdAt),
}

export function PlatformQuotaPoliciesPage() {
  useLanguage()
  const query = useQuotaPolicies()
  const [editingPolicy, setEditingPolicy] = useState<QuotaPolicy | null>(null)
  const [isCreating, setIsCreating] = useState(false)
  const policies = query.data?.items ?? []
  const state = useListState(listConfig)
  const list = useClientList(policies, state, {
    searchText: policy => `${policy.name} ${policy.key}`,
    comparators, tieBreak: (a, b) => compareText(a.name, b.name) || compareText(a.policyId, b.policyId),
  })
  const columns: Column<QuotaPolicy>[] = [
    { key: 'name', header: tx('Politika'), sortable: true, wrap: true, render: (policy) => <div className="space-y-1"><span className="font-semibold">{policy.name}</span>{policy.isDefault && <div><Badge tone="accent">{tx("Varsayılan")}</Badge></div>}</div> },
    { key: 'status', card: 'status', header: tx('Durum'), sortable: true, render: (policy) => <StatusPill level={policy.enabled ? 'safe' : 'neutral'}>{policy.enabled ? tx('Etkin') : tx('Devre dışı')}</StatusPill> },
    { key: 'enforcement', header: tx('Sınıra ulaşınca'), sortable: true, wrap: true, render: (policy) => enforcementModeLabels[policy.enforcementMode] },
    { key: 'created', header: tx('Oluşturulma'), sortable: true, render: policy => formatDateTime(policy.createdAt) },
    { key: 'technical', header: tx('Teknik ayrıntılar'), selfLabeled: true, wrap: true, render: (policy) => <TechnicalDetails items={[
      { label: tx("Politika kimliği"), value: policy.policyId },
      { label: tx("Politika anahtarı"), value: policy.key },
      { label: tx("Politika sürümü"), value: String(policy.version) },
      { label: tx("Hesaplama modu"), value: accountingModeLabels[policy.accountingMode] },
      { label: tx("Uyarı eşiği (%)"), value: String(policy.warningThresholdPercent) },
      { label: tx("Kritik eşik (%)"), value: String(policy.criticalThresholdPercent) },
      { label: tx("Kapsam"), value: scopeLabels(policy.includedStorageScopes).join('; ') },
      { label: tx("Oluşturulma zamanı"), value: formatDateTime(policy.createdAt) },
      { label: tx("Güncellendi"), value: formatDateTime(policy.updatedAt) },
    ]} /> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: (policy) => <Button variant="outline" size="sm" onClick={() => setEditingPolicy(policy)}>{tx("Düzenle")}</Button> },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Kota politikaları")} description={tx("Tenant'ların depolama hesaplama yöntemlerini, eşiklerini ve engelleme kurallarını yönetin.")} actions={<>
      <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw aria-hidden="true" className={`size-4 ${query.isFetching ? 'animate-spin motion-reduce:animate-none' : ''}`} />{tx("Yenile")}</Button>
      <Button onClick={() => setIsCreating(true)}><Plus aria-hidden="true" className="size-4" />{tx("Yeni politika")}</Button>
    </>} />
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {query.data && (policies.length === 0
        ? <ListEmpty message={tx("Tanımlı kota politikası bulunamadı.")} />
        : <div className="space-y-4">
          <ListToolbar state={state} label={tx('Kota politikaları')} total={list.total} search={{ label: tx('Politika ara'), placeholder: tx('Politika adı veya anahtarı') }} />
          <Section title={tx("Kota politikaları")} description={tx("{0} politika · Değişiklikler mevcut kullanım hesaplamalarını etkileyebilir.", { "0": policies.length })} flush className="overflow-hidden"><DataList className={bareListClass} rows={list.rows} columns={columns} sort={sortProps(state)} keyExtractor={(policy) => policy.policyId} emptyMessage={tx("Aramayla eşleşen kayıt yok.")} onClearFilters={state.filtering ? state.clear : undefined} /></Section>
        </div>)}
    </QueryState>
    {isCreating && <QuotaPolicyDialog onClose={() => setIsCreating(false)} />}
    {editingPolicy && <QuotaPolicyDialog policy={editingPolicy} onClose={() => setEditingPolicy(null)} />}
  </div>
}
