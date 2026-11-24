import { useState, type FormEvent } from 'react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { TextInput } from '@/components/ui/text-input'
import { platformCrumb } from '@/features/platform/crumbs'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { useUrlParams } from '@/features/lists/url-params'
import { CreatePlatformShareDialog } from '@/features/shares/create-platform-share-dialog'
import {
  useAuditRetention,
  usePlatformShares,
  useUpdateAuditRetention,
} from '@/features/shares/hooks'
import { downloadAttemptsListConfig } from '@/features/shares/download-attempts-config'
import { DownloadAttemptsSection } from '@/features/shares/download-attempts-section'
import { SharesTable } from '@/features/shares/shares-table'
import { platformOnlyMessage } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'
export function PlatformSharesPage() {
  useLanguage()
  const { get, update } = useUrlParams()
  const tenantId = get('tenantId')
  const [createOpen, setCreateOpen] = useState(false)
  const [retentionOverride, setRetentionOverride] = useState<string | null>(null)
  const [retentionError, setRetentionError] = useState<string | null>(null)

  const query = usePlatformShares({ tenantId })
  const shares = query.data ?? []

  const retentionQuery = useAuditRetention()
  const updateRetention = useUpdateAuditRetention()
  const retentionValue = retentionOverride ?? String(retentionQuery.data?.retentionDays ?? 365)

  function handleRetentionSubmit(event: FormEvent) {
    event.preventDefault()
    setRetentionError(null)
    const days = Number(retentionValue)
    if (!Number.isInteger(days) || days < 1 || days > 36500) {
      setRetentionError(tx('Saklama süresi en az 1 gün olmalıdır.'))
      return
    }
    updateRetention.mutate(days, {
      onSuccess: (updated) => {
        setRetentionOverride(String(updated.retentionDays))
      },
    })
  }
  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[platformCrumb, { label: tx('Paylaşılan yedekler') }]}
        title={tx('Paylaşılan yedekler')}
        actions={
          <Button onClick={() => setCreateOpen(true)}>
            {tx('Paylaşım oluştur')}
          </Button>
        }
      />


      <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
        <SharesTable
          shares={shares}
          showTenant
          tenantFilter={<TenantPicker value={tenantId} onChange={(value) => update({ tenantId: value, [downloadAttemptsListConfig.pageKey]: '' })} />}
          emptyMessage={
            tenantId
              ? tx('Seçilen filtrelere uygun paylaşım bulunamadı.')
              : tx('Henüz paylaşım yapılmamış.')
          }
        />
      </QueryState>

      <DownloadAttemptsSection scope={{ route: 'platform', tenantId: tenantId || undefined }} description={tx('Paylaşımlar ve yedek dosyaları üzerinden yapılan tüm indirme girişimleri.')} />

      <details className="group">
        <summary className="w-fit cursor-pointer rounded-sm py-1 font-medium text-primary underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">{tx('Denetim ve paylaşım geçmişi saklama süresi')}</summary>
      <Section
        title={tx('Denetim ve paylaşım geçmişi saklama süresi')}
        description={tx('Paylaşım geçmişi ve denetim kayıtlarının sistem genelinde saklanma süresi (varsayılan 365 gün).')}
      >
        <form onSubmit={handleRetentionSubmit} noValidate className="space-y-4">
          <Field label={tx('Saklama süresi (gün)')} error={retentionError ?? undefined}>
            {(control) => (
              <TextInput
                {...control}
                type="number"
                min={1}
                max={36500}
                step={1}
                value={retentionValue}
                onChange={(e) => setRetentionOverride(e.target.value)}
              />
            )}
          </Field>
          {updateRetention.isSuccess && (
            <Notice tone="success">{tx('Saklama süresi güncellendi.')}</Notice>
          )}
          <Button type="submit" disabled={updateRetention.isPending}>
            {updateRetention.isPending ? tx('Kaydediliyor…') : tx('Saklama süresini kaydet')}
          </Button>
        </form>
      </Section>
      </details>

      {createOpen && (
        <CreatePlatformShareDialog
          initialTenantId={tenantId}
          onClose={() => setCreateOpen(false)}
        />
      )}
    </div>
  )
}
