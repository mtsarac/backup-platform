import { useState } from 'react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Filters } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { Select } from '@/components/ui/text-input'
import { platformCrumb } from '@/features/platform/crumbs'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { useListParams } from '@/features/platform/use-list-params'
import { CreatePlatformShareDialog } from '@/features/shares/create-platform-share-dialog'
import { usePlatformShares } from '@/features/shares/hooks'
import { SharesTable } from '@/features/shares/shares-table'
import { platformOnlyMessage } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'

export function PlatformSharesPage() {
  useLanguage()
  const { get, update } = useListParams()
  const tenantId = get('tenantId')
  const status = get('status')
  const [createOpen, setCreateOpen] = useState(false)

  const query = usePlatformShares({ tenantId, status })
  const shares = query.data ?? []

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[platformCrumb, { label: tx('Paylaşılan Yedekler') }]}
        title={tx('Paylaşılan Yedekler')}
        actions={
          <Button onClick={() => setCreateOpen(true)}>
            {tx('Paylaşım oluştur')}
          </Button>
        }
      />

      <Filters
        label={tx('Paylaşım filtreleri')}
        onClear={tenantId || status ? () => update({ tenantId: '', status: '' }) : undefined}
      >
        <TenantPicker value={tenantId} onChange={(value) => update({ tenantId: value })} />
        <div className="space-y-1">
          <label className="text-xs font-medium text-text-muted">{tx('Durum')}</label>
          <Select value={status} onChange={(e) => update({ status: e.target.value })}>
            <option value="">{tx('Tümü')}</option>
            <option value="Active">{tx('Aktif')}</option>
            <option value="Expired">{tx('Süresi doldu')}</option>
            <option value="Revoked">{tx('İptal edildi')}</option>
          </Select>
        </div>
      </Filters>

      <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
        <SharesTable
          shares={shares}
          showTenant
          emptyMessage={
            tenantId || status
              ? tx('Seçilen filtrelere uygun paylaşım bulunamadı.')
              : tx('Henüz paylaşım yapılmamış.')
          }
        />
      </QueryState>

      {createOpen && (
        <CreatePlatformShareDialog
          initialTenantId={tenantId}
          onClose={() => setCreateOpen(false)}
        />
      )}
    </div>
  )
}
