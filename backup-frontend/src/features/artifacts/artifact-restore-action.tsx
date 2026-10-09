import { RotateCcw, AlertTriangle } from 'lucide-react'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { useTenant } from '@/features/tenancy/use-tenant'
import type { BackupArtifact } from './types'

export function ArtifactRestoreAction({
  tenantId,
  artifact,
}: {
  tenantId: string
  artifact: BackupArtifact
}) {
  const { canOperate } = useTenant()

  if (!canOperate) {
    return (
      <section aria-label="Geri yükleme yetkisi" className="rounded-lg border border-border bg-surface p-4 text-sm text-text-muted">
        <p>Geri yükleme (Restore Backup) işlemi Operator, Admin veya Owner rolü gerektirir. Viewer rolü bu işlem için yetkili değildir.</p>
      </section>
    )
  }

  let ineligibilityReason: string | null = null
  if (artifact.state === 'Deleted') {
    ineligibilityReason = 'Bu backup dosyasının tüm kopyaları silindiği için geri yüklenemez.'
  } else if (artifact.state === 'DeletePending') {
    ineligibilityReason = 'Bu backup silme bekleme süresindedir (DeletePending). Yeni geri yükleme başlatılamaz.'
  } else if (artifact.backupType !== 'Full') {
    ineligibilityReason = `Yalnızca tam (Full) backup türü geri yüklenebilir (Mevcut tür: ${artifact.backupType}).`
  } else if (artifact.replicas.length === 0) {
    ineligibilityReason = 'Geri yüklenebilecek replika kaydı bulunamadı (Missing replica).'
  } else if (!artifact.replicas.some((r) => r.verificationState === 'Verified')) {
    ineligibilityReason = 'Replika bütünlük doğrulaması başarısız veya tamamlanmamış (Verification failed).'
  }

  return (
    <section aria-label="Geri yükleme eylemi" className="rounded-lg border border-border bg-surface p-4 sm:p-5">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h2 className="text-base font-semibold text-text-heading flex items-center gap-2">
            <RotateCcw className="size-4 text-primary" />
            Geri Yükleme (Restore)
          </h2>
          <p className="mt-1 text-sm text-text-muted">
            Bu backup dosyasını hedef SQL Server instance'ına yeni veritabanı olarak veya mevcut veritabanının üzerine geri yükleyin.
          </p>
        </div>
        <div>
          {ineligibilityReason ? (
            <Button disabled variant="outline">
              Restore Backup
            </Button>
          ) : (
            <Link to={`/tenants/${tenantId}/artifacts/${artifact.id}/restore`}>
              <Button className="inline-flex items-center gap-2">
                <RotateCcw className="size-4" />
                Restore Backup
              </Button>
            </Link>
          )}
        </div>
      </div>

      {ineligibilityReason && (
        <div role="status" className="mt-3 flex items-start gap-2 rounded-md border border-warning/30 bg-warning-subtle p-3 text-xs text-warning">
          <AlertTriangle className="size-4 shrink-0 mt-0.5" />
          <span>Bu backup şu anda geri yüklenemez: {ineligibilityReason}</span>
        </div>
      )}
    </section>
  )
}
