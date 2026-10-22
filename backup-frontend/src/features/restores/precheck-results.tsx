import { tx, useLanguage } from '@/lib/i18n'
import { AlertTriangle, CheckCircle2, MinusCircle, RotateCcw, XCircle } from 'lucide-react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts } from '@/components/ui/section'
import { restoreFailureMessage, safeRestoreCode } from './presentation'
import type { RestoreResult, RestoreView } from './types'

type PrecheckItem = RestoreResult['preCheck'][number]

const checkStatus = {
  Passed: { get label() { return tx("Geçti") }, icon: CheckCircle2, className: 'text-success' },
  Warning: { get label() { return tx("Uyarı") }, icon: AlertTriangle, className: 'text-warning' },
  Failed: { get label() { return tx("Başarısız") }, icon: XCircle, className: 'text-error' },
  Skipped: { get label() { return tx("Atlandı") }, icon: MinusCircle, className: 'text-text-muted' },
} as const

const columns: Column<PrecheckItem>[] = [
  { key: 'check', get header() { return tx("Kontrol") }, wrap: true, render: (item) => <span className="font-mono text-sm">{safeRestoreCode(item.checkId)}</span> },
  { key: 'status', get header() { return tx("Sonuç") }, render: (item) => {
    const { label, icon: Icon, className } = checkStatus[item.status]
    return <span className={`inline-flex items-center gap-1.5 font-medium ${className}`}><Icon aria-hidden="true" className="size-4" />{tx(label)}</span>
  } },
  { key: 'message', get header() { return tx("Açıklama") }, wrap: true, render: (item) => <span className="text-text-muted">{item.status === 'Failed' ? restoreFailureMessage(item.reasonCode) : item.message || safeRestoreCode(item.reasonCode)}</span> },
]

/** The precheck verdict first, the individual checks second: what happened, and what the user may do next. */
export function PrecheckResults({ data, failed, passed, onRecheck, recheckDisabled, fileArchive = false }: {
  data: RestoreView; failed: boolean; passed: boolean; onRecheck: () => void; recheckDisabled: boolean; fileArchive?: boolean
}) {
  useLanguage()
  const items = data.result?.preCheck ?? []
  const warnings = items.filter((item) => item.status === 'Warning')
  const source = data.result?.sourceBackup
  return <div className="space-y-4">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="flex items-center gap-2"><span className="font-medium text-text-heading">{tx("Durum:")}</span><StatusPill status={data.status} /></div>
      <Button variant="outline" size="sm" onClick={onRecheck} disabled={recheckDisabled}><RotateCcw aria-hidden="true" className="size-4" />{tx("Yeniden Kontrol Et")}</Button>
    </div>
    {failed && <Notice tone="error" title={tx("Ön kontrol başarısız oldu! Restore başlatılamaz.")}>{data.failureCode ? restoreFailureMessage(data.failureCode) : tx('Aşağıdaki kontrollerden en az biri başarısız oldu. Hataları giderdikten sonra tekrar deneyin.')}</Notice>}
    {passed && warnings.length > 0 && <Notice tone="warning" title={tx("Ön kontrol uyarılarla geçti.")}>{tx("Aşağıdaki uyarıları inceleyerek devam edebilirsiniz.")}</Notice>}
    {passed && warnings.length === 0 && <Notice tone="success" title={tx("Tüm ön kontroller başarıyla tamamlandı. Geri yükleme güvenle başlatılabilir.")} />}
    <DataTable rows={items} columns={columns} keyExtractor={(item) => `${item.checkId}:${item.reasonCode}:${item.status}:${items.indexOf(item)}`} emptyMessage={tx("Henüz kontrol sonucu bildirilmedi.")} />
    {source && <Facts columns={2}>
      <Fact label={fileArchive ? tx('Dosya kaynağı referansı') : tx("Kaynak Database")}>{source.databaseName}</Fact>
      {!fileArchive && <Fact label={tx("Hedef Database motoru sürümü")}>{data.result?.targetServer?.version ?? tx('Bilinmiyor')}</Fact>}
      <Fact label={tx("Backup doğrulandı")}>{data.result?.backupVerified ? tx('Evet') : tx('Hayır')}</Fact>
      <Fact label={tx("Checksum doğrulandı")}>{data.result?.checksumVerified ? tx('Evet') : tx('Hayır')}</Fact>
    </Facts>}
  </div>
}
