import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { agentDate } from '@/features/agents/format'
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
    {
      key: 'name',
      header: 'Politika',
      wrap: true,
      render: (policy) => (
        <div>
          <div className="flex items-center gap-2">
            <span className="font-semibold text-text-heading">{policy.name}</span>
            {policy.isDefault && <Badge tone="info">Varsayılan</Badge>}
            <Badge tone={policy.enabled ? 'success' : 'neutral'}>
              {policy.enabled ? 'Etkin' : 'Devre Dışı'}
            </Badge>
          </div>
          <div className="text-xs text-text-muted">
            Anahtar: <code>{policy.key}</code> · Sürüm: v{policy.version}
          </div>
        </div>
      ),
    },
    {
      key: 'accounting',
      header: 'Hesaplama Modu',
      render: (policy) => (
        <Badge tone="neutral">{accountingModeLabels[policy.accountingMode]}</Badge>
      ),
    },
    {
      key: 'enforcement',
      header: 'Uygulama Modu',
      render: (policy) => (
        <span className="text-sm">{enforcementModeLabels[policy.enforcementMode]}</span>
      ),
    },
    {
      key: 'thresholds',
      header: 'Eşikler',
      render: (policy) => (
        <div className="text-xs space-y-0.5">
          <div>Warning: %{policy.warningThresholdPercent}</div>
          <div>Critical: %{policy.criticalThresholdPercent}</div>
        </div>
      ),
    },
    {
      key: 'scope',
      header: 'Kapsam',
      wrap: true,
      render: (policy) => (
        <div className="text-xs text-text-muted">
          {scopeLabels(policy.includedStorageScopes).join('; ')}
        </div>
      ),
    },
    {
      key: 'updatedAt',
      header: 'Güncellendi',
      render: (policy) => (
        <span className="text-xs text-text-muted">{agentDate(policy.updatedAt)}</span>
      ),
    },
    {
      key: 'actions',
      header: 'İşlemler',
      render: (policy) => (
        <Button
          variant="outline"
          size="sm"
          onClick={() => setEditingPolicy(policy)}
        >
          Düzenle
        </Button>
      ),
    },
  ]

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold text-text-heading">Kota Politikaları</h1>
          <p className="mt-1 text-sm text-text-muted">
            Tenant'ların depolama hesaplama yöntemlerini, eşiklerini ve engelleme kurallarını yönetin.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            onClick={() => void query.refetch()}
            disabled={query.isFetching}
          >
            <RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin' : ''}`} />
            Yenile
          </Button>
          <Button onClick={() => setIsCreating(true)}>
            <Plus className="size-4" />
            Yeni Politika
          </Button>
        </div>
      </div>

      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {query.data && (
          <DataTable
            rows={policies}
            columns={columns}
            keyExtractor={(p) => p.policyId}
            emptyMessage="Tanımlı kota politikası bulunamadı."
          />
        )}
      </PlatformQueryState>

      {isCreating && <QuotaPolicyDialog onClose={() => setIsCreating(false)} />}
      {editingPolicy && (
        <QuotaPolicyDialog
          policy={editingPolicy}
          onClose={() => setEditingPolicy(null)}
        />
      )}
    </div>
  )
}
