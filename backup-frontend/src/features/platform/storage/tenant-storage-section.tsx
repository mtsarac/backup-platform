import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw, RotateCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { agentDate } from '@/features/agents/format'
import { accountingModeLabels, enforcementModeLabels, scopeLabels } from '@/features/storage/labels'
import { QuotaSummary, StandingBadge } from '@/features/storage/quota-summary'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { errorMessage } from '@/lib/api/failure-messages'
import { usePlatformTenantQuota, useRebuildUsage, useReconcileUsage } from './hooks'

export function TenantStorageSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
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
            ? tx("Reconcile tamamlandı: sapma düzeltildi ({0} rezerve temizlendi).", { "0": res.reservationsReleased })
            : tx('Reconcile tamamlandı: sapma tespit edilmedi.'),
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
          tx("Rebuild tamamlandı: kullanım yeniden hesaplandı (V{0}, {1} rezerve temizlendi).", { "0": res.policyVersion, "1": res.reservationsReleased }),
        )
      },
    })
  }

  return (
    <Section title={<span className="inline-flex flex-wrap items-center gap-2">{tx("Depolama ve Kota")}{' '}{quota && <StandingBadge quota={quota} />}</span>} description={tx("Kullanılan ve rezerve alan; geçerli politika ve Backup başlatma durumu.")} actions={<>
          <Button
            variant="outline"
            size="sm"
            disabled={query.isFetching}
            onClick={() => void query.refetch()}
            title={tx("Kullanım verisini yenile")}
          >
            <RefreshCw className={`size-3.5 ${query.isFetching ? 'animate-spin' : ''}`} />{tx("Yenile")}</Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              reconcile.reset()
              setConfirmOp('reconcile')
            }}
          >{tx("Reconcile")}</Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              rebuild.reset()
              setConfirmOp('rebuild')
            }}
          >
            <RotateCcw className="size-3.5" />{tx("Rebuild")}</Button>
      </>}>
      <div className="space-y-4">
      {lastResult && <Notice tone="success">{lastResult}</Notice>}

      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {quota && (
          <div className="space-y-4">
            <QuotaSummary quota={quota} />

            <details className="rounded-lg bg-surface-2 p-4">
              <summary className="cursor-pointer text-sm font-medium text-text-heading">{tx("Kota politikası ve kapsam")}</summary>
              <div className="pt-4"><Facts columns={2}>
                <Fact label={tx("Kota Politikası")}><Link to={platformPaths.quotaPolicies} className="font-medium text-primary hover:underline">{quota.policy.key} (v{quota.policy.version})</Link></Fact>
                <Fact label={tx("Hesaplama Modu")}>{accountingModeLabels[quota.accountingMode]}</Fact>
                <Fact label={tx("Uygulama Modu")}>{enforcementModeLabels[quota.enforcementMode]}</Fact>
                <Fact label={tx("Kapsam")}>{scopeLabels(quota.includedStorageScopes).join('; ')}</Fact>
              </Facts></div>
            </details>

            {!quota.newBackupsAllowed && <Notice tone="error">{tx("Yeni backup başlatma engellendi (red kodu:")}{' '}<code>{quota.denialCode ?? 'bilinmiyor'}</code>).</Notice>}

            <div className="text-xs text-text-muted">{tx("Son hesaplama zamanı:")}{' '}{agentDate(quota.calculatedAt)}
            </div>
          </div>
        )}
      </PlatformQueryState>

      <ConfirmDialog
        open={confirmOp === 'reconcile'}
        onClose={() => setConfirmOp(null)}
        title={tx("Depolama kullanımı doğrulansın mı (Reconcile)?")}
        description={tx("“{0}” tenant'ının aktif artifact ve replika kayıtları taranarak izlenen kullanım ile senkronize edilir. Zaman aşımına uğramış rezervasyonlar serbest bırakılır.", { "0": tenantName })}
        confirmLabel="Reconcile Çalıştır"
        pending={reconcile.isPending}
        error={reconcile.error ? errorMessage(reconcile.error) : undefined}
        onConfirm={runReconcile}
      />

      <ConfirmDialog
        open={confirmOp === 'rebuild'}
        onClose={() => setConfirmOp(null)}
        title={tx("Depolama kullanımı sıfırdan hesaplansın mı (Rebuild)?")}
        description={tx("“{0}” tenant'ının kullanım tablosu sıfırlanıp tüm artifact ve replikaları geçerli kota politikası ({1}) kurallarına göre baştan hesaplanacaktır. Bu işlem audit kaydı oluşturur.", { "0": tenantName, "1": quota?.policy.key ?? tx('varsayılan') })}
        confirmLabel="Rebuild Çalıştır"
        pending={rebuild.isPending}
        error={rebuild.error ? errorMessage(rebuild.error) : undefined}
        onConfirm={runRebuild}
      />
      </div>
    </Section>
  )
}
