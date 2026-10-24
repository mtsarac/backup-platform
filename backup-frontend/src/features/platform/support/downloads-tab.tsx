import { tx, useLanguage } from '@/lib/i18n'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { StatusPill } from '@/components/data/status-pill'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { bareListClass } from '@/features/platform/list-empty'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { failureMessage } from '@/lib/api/failure-messages'
import { useSupportDownloads } from './hooks'
import { supportScope } from './presentation'
import type { SupportDownload, SupportSession } from './types'
import { formatBytes, formatDateTime } from '@/lib/format'
import { QueryState } from '@/components/data/query-state'
import { errorMessage } from '@/lib/api/failure-messages'

export function SupportDownloadsTab({ session }: { session: SupportSession }) {
  useLanguage()
  const { page, update } = useListParams('downloadPage')
  const scope = supportScope(session, 'downloads')
  const query = useSupportDownloads(scope, { page, pageSize: PLATFORM_PAGE_SIZE })
  const columns: Column<SupportDownload>[] = [
    { key: 'filename', card: 'title', header: tx('Dosya'), wrap: true, render: item => item.filename },
    { key: 'artifact', header: tx('Yedek dosyası'), wrap: true, render: item => <div className="space-y-2"><button className="text-primary hover:underline" onClick={() => update({ tab: 'browser', view: 'artifacts', artifact: item.artifactId })}>{tx("Yedek dosyası ayrıntıları")}</button><TechnicalDetails items={[{ label: tx('Yedek dosyası kimliği'), value: item.artifactId }]} /></div> },
    { key: 'state', card: 'status', header: tx('Durum'), render: item => <StatusPill status={item.state} /> },
    { key: 'bytes', header: tx('Aktarım / Boyut'), render: item => `${formatBytes(item.transferredBytes)} / ${formatBytes(item.sizeBytes)}` },
    { key: 'failure', header: tx('Sonuç'), wrap: true, render: item => item.failureCode ? failureMessage(item.failureCode) : '—' },
    { key: 'created', card: 'hidden', header: tx('Başlangıç'), render: item => formatDateTime(item.createdAt) },
    { key: 'released', card: 'hidden', header: tx('Bitiş'), render: item => formatDateTime(item.releasedAt) },
  ]
  if (!scope.enabled) return <Notice>{tx("Download geçmişi için etkin oturum sahipliği ve ViewBackupMetadata veya DownloadBackup izni gerekir.")}</Notice>
  return <QueryState forbiddenMessage={errorMessage} isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
    <Section title={tx("Download geçmişi")} description={tx("Başlatılan indirmeler, aktarım miktarı ve sonuçları.")} flush className="overflow-hidden"><DataList className={bareListClass} rows={query.data.items} columns={columns} keyExtractor={item => item.downloadId} /></Section>
    <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ downloadPage: next })} />
  </div>}</QueryState>
}
