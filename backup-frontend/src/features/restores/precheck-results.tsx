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
  Passed: { label: 'Geçti', icon: CheckCircle2, className: 'text-success' },
  Warning: { label: 'Uyarı', icon: AlertTriangle, className: 'text-warning' },
  Failed: { label: 'Başarısız', icon: XCircle, className: 'text-error' },
  Skipped: { label: 'Atlandı', icon: MinusCircle, className: 'text-text-muted' },
} as const

const columns: Column<PrecheckItem>[] = [
  { key: 'check', header: 'Kontrol', wrap: true, render: (item) => <span className="font-mono text-sm">{safeRestoreCode(item.checkId)}</span> },
  { key: 'status', header: 'Sonuç', render: (item) => {
    const { label, icon: Icon, className } = checkStatus[item.status]
    return <span className={`inline-flex items-center gap-1.5 font-medium ${className}`}><Icon aria-hidden="true" className="size-4" />{label}</span>
  } },
  { key: 'message', header: 'Açıklama', wrap: true, render: (item) => <span className="text-text-muted">{item.status === 'Failed' ? restoreFailureMessage(item.reasonCode) : item.message || safeRestoreCode(item.reasonCode)}</span> },
]

/** The precheck verdict first, the individual checks second: what happened, and what the user may do next. */
export function PrecheckResults({ data, failed, passed, onRecheck, recheckDisabled }: {
  data: RestoreView; failed: boolean; passed: boolean; onRecheck: () => void; recheckDisabled: boolean
}) {
  const items = data.result?.preCheck ?? []
  const warnings = items.filter((item) => item.status === 'Warning')
  const source = data.result?.sourceBackup
  return <div className="space-y-4">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="flex items-center gap-2"><span className="font-medium text-text-heading">Durum:</span><StatusPill status={data.status} /></div>
      <Button variant="outline" size="sm" onClick={onRecheck} disabled={recheckDisabled}><RotateCcw aria-hidden="true" className="size-4" />Yeniden Kontrol Et</Button>
    </div>
    {failed && <Notice tone="error" title="Ön kontrol başarısız oldu! Restore başlatılamaz.">{data.failureCode ? restoreFailureMessage(data.failureCode) : 'Aşağıdaki kontrollerden en az biri başarısız oldu. Hataları giderdikten sonra tekrar deneyin.'}</Notice>}
    {passed && warnings.length > 0 && <Notice tone="warning" title="Ön kontrol uyarılarla geçti.">Aşağıdaki uyarıları inceleyerek devam edebilirsiniz.</Notice>}
    {passed && warnings.length === 0 && <Notice tone="success" title="Tüm ön kontroller başarıyla tamamlandı. Geri yükleme güvenle başlatılabilir." />}
    <DataTable rows={items} columns={columns} keyExtractor={(item) => `${item.checkId}:${item.reasonCode}:${item.status}:${items.indexOf(item)}`} emptyMessage="Henüz kontrol sonucu bildirilmedi." />
    {source && <Facts columns={2}>
      <Fact label="Kaynak Database">{source.databaseName}</Fact>
      <Fact label="Hedef SQL sürümü">{data.result?.targetServer?.version ?? 'Bilinmiyor'}</Fact>
      <Fact label="Header doğrulandı">{data.result?.backupVerified ? 'Evet' : 'Hayır'}</Fact>
      <Fact label="Checksum doğrulandı">{data.result?.checksumVerified ? 'Evet' : 'Hayır'}</Fact>
    </Facts>}
  </div>
}
