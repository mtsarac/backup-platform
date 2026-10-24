import { tx, useLanguage } from '@/lib/i18n'
import { AlertTriangle, CheckCircle2, MinusCircle, RotateCcw, XCircle } from 'lucide-react'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import type { TechnicalItem } from '@/components/ui/technical-details'
import { precheckLabel, safeRestoreCode } from './presentation'
import type { RestoreResult, RestoreView } from './types'
import { describeFailure, failureMessage } from '@/lib/api/failure-messages'

type PrecheckItem = RestoreResult['preCheck'][number]

const checkStatus = {
  Passed: { get label() { return tx("Geçti") }, icon: CheckCircle2, className: 'text-success' },
  Warning: { get label() { return tx("Uyarı") }, icon: AlertTriangle, className: 'text-warning' },
  Failed: { get label() { return tx("Başarısız") }, icon: XCircle, className: 'text-error' },
  Skipped: { get label() { return tx("Atlandı") }, icon: MinusCircle, className: 'text-text-muted' },
} as const

/** The engines' own wording and codes for each check; the table above says the same thing in everyday words. */
function technicalChecks(items: PrecheckItem[]): TechnicalItem[] {
  const seen = new Map<string, number>()
  return items.map((item) => {
    const count = (seen.get(item.checkId) ?? 0) + 1
    seen.set(item.checkId, count)
    return { label: count > 1 ? `${item.checkId} (${count})` : item.checkId, value: [item.status, safeRestoreCode(item.reasonCode), item.message].filter(Boolean).join(' · ') }
  })
}

const columns: Column<PrecheckItem>[] = [
  { key: 'check', get header() { return tx("Kontrol") }, wrap: true, render: (item) => <span className="text-text-heading">{precheckLabel(item.checkId)}</span> },
  { key: 'status', card: 'status', get header() { return tx("Sonuç") }, render: (item) => {
    const { label, icon: Icon, className } = checkStatus[item.status]
    return <span className={`inline-flex items-center gap-1.5 font-medium ${className}`}><Icon aria-hidden="true" className="size-4" />{tx(label)}</span>
  } },
  { key: 'message', get header() { return tx("Açıklama") }, wrap: true, render: (item) => <span className="text-text-muted">{item.status === 'Failed' ? failureMessage(item.reasonCode, 'restore') : item.status === 'Warning' ? item.message : ''}</span> },
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
    {failed && <ErrorNotice title={tx("Ön kontrol başarısız oldu! Restore başlatılamaz.")} {...(data.failureCode ? describeFailure(data.failureCode, 'restore') : { what: tx('Aşağıdaki kontrollerden en az biri başarısız oldu. Hataları giderdikten sonra tekrar deneyin.'), dataImpact: fileArchive ? tx('Geri yükleme başlamadı; hedefteki dosyalar değişmedi.') : tx('Geri yükleme başlamadı; hedef veritabanı değişmedi.'), action: tx('Başarısız kontrolleri giderip ön kontrolü yeniden çalıştırın.'), technical: [] })} />}
    {passed && warnings.length > 0 && <Notice tone="warning" title={tx("Ön kontrol uyarılarla geçti.")}>{tx("Aşağıdaki uyarıları inceleyerek devam edebilirsiniz.")}</Notice>}
    {passed && warnings.length === 0 && <Notice tone="success" title={tx("Tüm ön kontroller başarıyla tamamlandı. Geri yükleme güvenle başlatılabilir.")} />}
    <DataList rows={items} columns={columns} keyExtractor={(item) => `${item.checkId}:${item.reasonCode}:${item.status}:${items.indexOf(item)}`} emptyMessage={tx("Henüz kontrol sonucu bildirilmedi.")} />
    {source && <Facts columns={2}>
      <Fact label={fileArchive ? tx('Dosya kaynağı referansı') : tx("Kaynak Database")}>{source.databaseName}</Fact>
      {!fileArchive && <Fact label={tx("Hedef Database motoru sürümü")}>{data.result?.targetServer?.version ?? tx('Bilinmiyor')}</Fact>}
      <Fact label={tx("Yedek doğrulandı")}>{data.result?.backupVerified ? tx('Evet') : tx('Hayır')}</Fact>
      <Fact label={tx("Yedek dosyasının bütünlüğü doğrulandı")}>{data.result?.checksumVerified ? tx('Evet') : tx('Hayır')}</Fact>
    </Facts>}
    <TechnicalDetails items={technicalChecks(items)} />
  </div>
}
