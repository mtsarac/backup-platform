import { useState, type FormEvent } from 'react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Filters } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Select, TextInput } from '@/components/ui/text-input'
import { platformCrumb } from '@/features/platform/crumbs'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { useListParams } from '@/features/platform/use-list-params'
import { CreatePlatformShareDialog } from '@/features/shares/create-platform-share-dialog'
import {
  useAuditRetention,
  usePlatformDownloadAttempts,
  usePlatformShares,
  useUpdateAuditRetention,
} from '@/features/shares/hooks'
import { DownloadAttemptsTable, SharesTable } from '@/features/shares/shares-table'
import { platformOnlyMessage } from '@/lib/api/failure-messages'
import { tx, useLanguage } from '@/lib/i18n'
export function PlatformSharesPage() {
  useLanguage()
  const { get, update } = useListParams()
  const tenantId = get('tenantId')
  const status = get('status')
  const shareFilter = get('shareId')
  const artifactFilter = get('artifactId')
  const userFilter = get('userId')
  const resultFilter = get('result')
  const [createOpen, setCreateOpen] = useState(false)
  const [retentionOverride, setRetentionOverride] = useState<string | null>(null)
  const [retentionError, setRetentionError] = useState<string | null>(null)

  const query = usePlatformShares({ tenantId, status })
  const shares = query.data ?? []

  const attemptsQuery = usePlatformDownloadAttempts({
    shareId: shareFilter || undefined,
    tenantId: tenantId || undefined,
    artifactId: artifactFilter || undefined,
    userId: userFilter || undefined,
    result: resultFilter || undefined,
  })
  const attempts = attemptsQuery.data?.pages.flatMap((page) => page.items) ?? []

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

      <Section
        title={tx('İndirme girişimleri')}
        description={tx('Paylaşımlar ve yedek dosyaları üzerinden yapılan tüm indirme girişimleri.')}
      >
        <div className="space-y-4">
          <Filters
            label={tx('İndirme girişimi filtreleri')}
            onClear={
              shareFilter || artifactFilter || userFilter || resultFilter
                ? () => update({ shareId: '', artifactId: '', userId: '', result: '' })
                : undefined
            }
          >
            <div className="space-y-1">
              <label className="text-xs font-medium text-text-muted">{tx('Paylaşım')}</label>
              <TextInput
                aria-label={tx('Paylaşım ID')}
                value={shareFilter}
                onChange={(e) => update({ shareId: e.target.value })}
              />
            </div>
            <div className="space-y-1">
              <label className="text-xs font-medium text-text-muted">{tx('Yedek dosyası')}</label>
              <TextInput
                aria-label={tx('Yedek dosyası ID')}
                value={artifactFilter}
                onChange={(e) => update({ artifactId: e.target.value })}
              />
            </div>
            <div className="space-y-1">
              <label className="text-xs font-medium text-text-muted">{tx('Kullanıcı')}</label>
              <TextInput
                aria-label={tx('Kullanıcı ID')}
                value={userFilter}
                onChange={(e) => update({ userId: e.target.value })}
              />
            </div>
            <div className="space-y-1">
              <label className="text-xs font-medium text-text-muted">{tx('Sonuç')}</label>
              <Select
                aria-label={tx('Sonuç')}
                value={resultFilter}
                onChange={(e) => update({ result: e.target.value })}
              >
                <option value="">{tx('Tümü')}</option>
                <option value="Completed">{tx('Tamamlandı')}</option>
                <option value="Failed">{tx('Başarısız')}</option>
                <option value="Cancelled">{tx('İptal edildi')}</option>
                <option value="Refused">{tx('Reddedildi')}</option>
              </Select>
            </div>
          </Filters>

          <QueryState forbiddenMessage={platformOnlyMessage} isPending={attemptsQuery.isPending} error={attemptsQuery.error}>
            <DownloadAttemptsTable
              attempts={attempts}
              showTenant
              onLoadMore={attemptsQuery.hasNextPage ? () => { void attemptsQuery.fetchNextPage() } : undefined}
              loadingMore={attemptsQuery.isFetchingNextPage}
              emptyMessage={tx('Henüz indirme girişimi kaydı yok.')}
            />
          </QueryState>
        </div>
      </Section>

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

      {createOpen && (
        <CreatePlatformShareDialog
          initialTenantId={tenantId}
          onClose={() => setCreateOpen(false)}
        />
      )}
    </div>
  )
}
