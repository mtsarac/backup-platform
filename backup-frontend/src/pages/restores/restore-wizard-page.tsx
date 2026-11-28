import { tx, useLanguage } from '@/lib/i18n'
import { useLocation, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { PageHeader } from '@/components/ui/page-header'
import { useBackup } from '@/features/backups/hooks'
import { useRestoreTargets } from '@/features/restores/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { FileRestoreWizard } from '@/features/restores/file-restore-wizard'
import { RestoreWizard } from '@/features/restores/restore-wizard'
import type { RestorePrefill } from '@/features/restores/types'

export function RestoreWizardPage() {
  useLanguage()
  const { id = '' } = useParams<{ id: string }>()
  const { tenantId } = useTenant()
  const prefill = (useLocation().state as { restorePrefill?: RestorePrefill } | null)?.restorePrefill
  const runQuery = useBackup(id)
  const latest = runQuery.data?.attempts.flatMap(attempt => attempt.artifacts).at(-1)
  const targetsQuery = useRestoreTargets(latest?.id ?? '')
  // A PostgreSQL artifact may carry no database name; the run's configuration always does.
  const artifact = latest && (targetsQuery.data?.engine === 'file-system' ? latest : { ...latest, databaseName: latest.databaseName ?? runQuery.data?.configuration.database })


  const title = targetsQuery.data?.engine === 'file-system' ? tx('Dosyaları geri yükle') : targetsQuery.data ? tx('Veritabanını geri yükle') : tx('Yedek dosyasını geri yükle')
  return (
    <div className="space-y-6">
      <PageHeader title={title}
        description={targetsQuery.data?.engine === 'file-system' ? tx('Dosya arşivini özgün veya alternatif konuma, tümüyle ya da seçili yollarla geri yükleyin.') : targetsQuery.data?.engine === 'postgres' ? tx("PostgreSQL pg_dump custom arşivini pg_restore ile hedef PostgreSQL sunucusuna geri yükleyin.") : tx("Doğrulanmış backup'ı aynı Database motoruna güvenli ve denetlenebilir adımlarla geri yükleyin.")}
        crumbs={[{ label: tx('Yedekleme denemeleri'), to: `/tenants/${tenantId}/backup-runs` }, { label: artifact?.databaseName ?? tx('Yedekleme denemesi'), to: `/tenants/${tenantId}/backup-runs/${id}` }, { label: tx('Geri yükleme') }]}
      />
      <QueryState isPending={runQuery.isPending || (Boolean(artifact) && targetsQuery.isPending)} error={runQuery.error || targetsQuery.error} notFound={{ backTo: `/tenants/${tenantId}/backup-runs`, backLabel: tx('Yedekleme denemeleri') }} empty={!runQuery.isPending && !artifact} emptyState={<p>{tx("Bu denemede kayıtlı yedek dosyası yok.")}</p>}>
        {artifact && targetsQuery.data && (
          targetsQuery.data.engine === 'file-system' ? <FileRestoreWizard key={`${tenantId}:${id}`} tenantId={tenantId} artifact={artifact} targets={targetsQuery.data} /> : <RestoreWizard
            key={`${tenantId}:${id}`}
            tenantId={tenantId}
            artifact={artifact}
            targets={targetsQuery.data}
            prefill={prefill}
          />
        )}
      </QueryState>
    </div>
  )
}
