import { useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw, RotateCcw, AlertTriangle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { accountingModeLabels, enforcementModeLabels, scopeLabels } from '@/features/storage/labels'
import { QuotaSummary, StandingBadge } from '@/features/storage/quota-summary'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { errorMessage } from '@/lib/api/failure-messages'
import { usePlatformTenantQuota, useRebuildUsage, useReconcileUsage } from './hooks'

export function TenantStorageSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  const query = usePlatformTenantQuota(tenantId)
  const reconcile = useReconcileUsage(tenantId)
  const rebuild = useRebuildUsage(tenantId)
  const [confirmOp, setConfirmOp] = useState<'reconcile' | 'rebuild' | null>(null)
  const [lastResult, setLastResult] = useState<string | null>(null)

  const quota = query.data

  function runReconcile() {
    setLastResult(null)
    reconcile.mutate(undefined, {
      onSuccess: (res) => {
        setConfirmOp(null)
        setLastResult(
          res.drifted
            ? `Reconcile tamamlandı: sapma düzeltildi (${res.reservationsReleased} rezerve temizlendi).`
            : 'Reconcile tamamlandı: sapma tespit edilmedi.',
        )
      },
    })
  }

  function runRebuild() {
    setLastResult(null)
    rebuild.mutate(undefined, {
      onSuccess: (res) => {
        setConfirmOp(null)
        setLastResult(
          `Rebuild tamamlandı: kullanım yeniden hesaplandı (V${res.policyVersion}, ${res.reservationsReleased} rezerve temizlendi).`,
        )
      },
    })
  }

  return (
    <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="storage-heading">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <h2 id="storage-heading" className="font-medium text-text-heading">Depolama ve Kota</h2>
          {quota && <StandingBadge quota={quota} />}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={query.isFetching}
            onClick={() => void query.refetch()}
            title="Kullanım verisini yenile"
          >
            <RefreshCw className={`size-3.5 ${query.isFetching ? 'animate-spin' : ''}`} />
            Yenile
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              reconcile.reset()
              setConfirmOp('reconcile')
            }}
          >
            Reconcile
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              rebuild.reset()
              setConfirmOp('rebuild')
            }}
          >
            <RotateCcw className="size-3.5" />
            Rebuild
          </Button>
        </div>
      </div>

      {lastResult && (
        <p role="status" className="rounded-md border border-success/30 bg-success-subtle p-2 text-sm text-success">
          {lastResult}
        </p>
      )}

      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {quota && (
          <div className="space-y-4">
            <QuotaSummary quota={quota} />

            <div className="grid gap-3 rounded-lg border border-border bg-surface-2 p-3 text-xs sm:grid-cols-2 lg:grid-cols-4">
              <div>
                <span className="text-text-muted">Kota Politikası:</span>{' '}
                <Link to={platformPaths.quotaPolicies} className="font-medium text-primary hover:underline">
                  {quota.policy.key} (v{quota.policy.version})
                </Link>
              </div>
              <div>
                <span className="text-text-muted">Hesaplama Modu:</span>{' '}
                <span className="font-medium">{accountingModeLabels[quota.accountingMode]}</span>
              </div>
              <div>
                <span className="text-text-muted">Uygulama Modu:</span>{' '}
                <span className="font-medium">{enforcementModeLabels[quota.enforcementMode]}</span>
              </div>
              <div>
                <span className="text-text-muted">Kapsam:</span>{' '}
                <span className="font-medium">{scopeLabels(quota.includedStorageScopes).join('; ')}</span>
              </div>
            </div>

            {!quota.newBackupsAllowed && (
              <div className="flex items-center gap-2 rounded-md border border-error/30 bg-error-subtle p-2.5 text-xs text-error">
                <AlertTriangle className="size-4 shrink-0" />
                <span>
                  Yeni backup başlatma engellendi (red kodu: <code>{quota.denialCode ?? 'bilinmiyor'}</code>).
                </span>
              </div>
            )}

            <div className="text-xs text-text-muted">
              Son hesaplama zamanı: {agentDate(quota.calculatedAt)}
            </div>
          </div>
        )}
      </PlatformQueryState>

      <ConfirmDialog
        open={confirmOp === 'reconcile'}
        onClose={() => setConfirmOp(null)}
        title="Depolama kullanımı doğrulansın mı (Reconcile)?"
        description={`“${tenantName}” tenant'ının aktif artifact ve replika kayıtları taranarak izlenen kullanım ile senkronize edilir. Zaman aşımına uğramış rezervasyonlar serbest bırakılır.`}
        confirmLabel="Reconcile Çalıştır"
        pending={reconcile.isPending}
        error={reconcile.error ? errorMessage(reconcile.error) : undefined}
        onConfirm={runReconcile}
      />

      <ConfirmDialog
        open={confirmOp === 'rebuild'}
        onClose={() => setConfirmOp(null)}
        title="Depolama kullanımı sıfırdan hesaplansın mı (Rebuild)?"
        description={`“${tenantName}” tenant'ının kullanım tablosu sıfırlanıp tüm artifact ve replikaları geçerli kota politikası (${quota?.policy.key ?? 'varsayılan'}) kurallarına göre baştan hesaplanacaktır. Bu işlem audit kaydı oluşturur.`}
        confirmLabel="Rebuild Çalıştır"
        pending={rebuild.isPending}
        error={rebuild.error ? errorMessage(rebuild.error) : undefined}
        onConfirm={runRebuild}
      />
    </section>
  )
}
