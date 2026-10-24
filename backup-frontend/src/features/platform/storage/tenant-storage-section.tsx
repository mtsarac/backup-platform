import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw, RotateCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { accountingModeLabels, enforcementModeLabels, scopeLabels } from '@/features/storage/labels'
import { QuotaSummary, StandingBadge } from '@/features/storage/quota-summary'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { usePlatformTenantQuota, useRebuildUsage, useReconcileUsage } from './hooks'
import { formatDateTime } from '@/lib/format'

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

            {!quota.newBackupsAllowed && <ErrorNotice what={tx("Yeni yedekleme başlatma engellendi.")} dataImpact={tx("Mevcut yedekler silinmedi ve geri yüklenebilir.")} action={tx("Kota ve abonelik durumunu gözden geçirin.")} technical={[{ label: tx("Red kodu"), value: quota.denialCode ?? tx("bilinmiyor") }]} />}

            <div className="text-xs text-text-muted">{tx("Son hesaplama zamanı:")}{' '}{formatDateTime(quota.calculatedAt, tx('Henüz bildirilmedi'))}
            </div>
          </div>
        )}
      </PlatformQueryState>

      <ConfirmDialog
        open={confirmOp === 'reconcile'}
        onClose={() => setConfirmOp(null)}
        title={tx("Depolama kullanımı doğrulansın mı (Reconcile)?")}
        consequence={{
          what: tx("“{0}” çalışma alanının aktif yedek dosyaları ve kopyaları taranarak izlenen kullanım ile eşitlenir; süresi dolan rezervasyonlar serbest bırakılır.", { "0": tenantName }),
          who: tx("Yalnızca bu çalışma alanının kota hesabı etkilenir; yedekler değişmez."),
          reversible: tx("Evet. İşlem yalnızca sayıları günceller; istediğiniz zaman yeniden çalıştırabilirsiniz."),
        }}
        confirmLabel={tx("Doğrulamayı çalıştır")}
        pending={reconcile.isPending}
        error={reconcile.error}
        onConfirm={runReconcile}
      />

      <ConfirmDialog
        open={confirmOp === 'rebuild'}
        onClose={() => setConfirmOp(null)}
        title={tx("Depolama kullanımı sıfırdan hesaplansın mı (Rebuild)?")}
        consequence={{
          what: tx("“{0}” çalışma alanının kullanım tablosu sıfırlanır; tüm yedek dosyaları ve kopyaları geçerli kota politikası ({1}) kurallarına göre baştan hesaplanır. İşlem denetim kaydı oluşturur.", { "0": tenantName, "1": quota?.policy.key ?? tx('varsayılan') }),
          who: tx("Yalnızca bu çalışma alanının kota hesabı etkilenir; yedekler değişmez. Hesap sürerken kullanım rakamları geçici olarak değişebilir."),
          reversible: tx("Evet. Hesabı istediğiniz zaman yeniden çalıştırabilirsiniz."),
        }}
        confirmLabel={tx("Sıfırdan hesapla")}
        pending={rebuild.isPending}
        error={rebuild.error}
        onConfirm={runRebuild}
      />
      </div>
    </Section>
  )
}
