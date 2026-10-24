import { tx, useLanguage } from '@/lib/i18n'
import { useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { PageHeader } from '@/components/ui/page-header'
import { useArtifact } from '@/features/artifacts/hooks'
import { useRestoreTargets } from '@/features/restores/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { FileRestoreWizard } from '@/features/restores/file-restore-wizard'
import { RestoreWizard } from '@/features/restores/restore-wizard'
import { AccessDenied } from '@/components/auth/access-denied'

export function RestoreWizardPage() {
  useLanguage()
  const { id = '' } = useParams<{ id: string }>()
  const { tenantId, canOperate } = useTenant()
  const artifactQuery = useArtifact(tenantId, id)
  const targetsQuery = useRestoreTargets(id)

  if (!canOperate) {
    return <div className="space-y-6">
      <PageHeader title={tx("Backup Geri Yükleme Sihirbazı")} crumbs={[{ label: tx('Backup dosyaları'), to: `/tenants/${tenantId}/artifacts` }, { label: tx('Backup detayları'), to: `/tenants/${tenantId}/artifacts/${id}` }, { label: tx('Geri yükleme') }]} />
      <AccessDenied what={tx("Bu sayfada geri yükleme (Restore) işlemi yapma yetkiniz yok. Restore başlatma yalnızca Owner, Admin ve Operator rollerine açıktır. Viewer rolü salt okunurdur.")} />
    </div>
  }

  return (
    <div className="space-y-6">
      <PageHeader title={tx("Backup Geri Yükleme Sihirbazı")}
        description={targetsQuery.data?.engine === 'file-system' ? tx('Dosya arşivini özgün veya alternatif konuma, tümüyle ya da seçili yollarla geri yükleyin.') : targetsQuery.data?.engine === 'postgres' ? tx("PostgreSQL pg_dump custom arşivini pg_restore ile hedef PostgreSQL sunucusuna geri yükleyin.") : tx("Doğrulanmış backup'ı aynı Database motoruna güvenli ve denetlenebilir adımlarla geri yükleyin.")}
        crumbs={[{ label: tx('Backup dosyaları'), to: `/tenants/${tenantId}/artifacts` }, { label: artifactQuery.data?.databaseName ?? tx('Backup detayları'), to: `/tenants/${tenantId}/artifacts/${id}` }, { label: tx('Geri yükleme') }]}
      />
      <QueryState isPending={artifactQuery.isPending || targetsQuery.isPending} error={artifactQuery.error || targetsQuery.error}>
        {artifactQuery.data && targetsQuery.data && (
          targetsQuery.data.engine === 'file-system' ? <FileRestoreWizard key={`${tenantId}:${id}`} tenantId={tenantId} artifact={artifactQuery.data} targets={targetsQuery.data} /> : <RestoreWizard
            key={`${tenantId}:${id}`}
            tenantId={tenantId}
            artifact={artifactQuery.data}
            targets={targetsQuery.data}
          />
        )}
      </QueryState>
    </div>
  )
}
