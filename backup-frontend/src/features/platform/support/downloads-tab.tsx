import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { StatusPill } from '@/components/data/status-pill'
import { artifactDate, artifactSize } from '@/features/artifacts/format'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { failureMessage } from '@/lib/api/failure-messages'
import { useSupportDownloads } from './hooks'
import { supportScope } from './presentation'
import { SupportQueryState } from './support-query-state'
import type { SupportDownload, SupportSession } from './types'

export function SupportDownloadsTab({ session }: { session: SupportSession }) {
  const { page, update } = useListParams('downloadPage')
  const scope = supportScope(session, 'downloads')
  const query = useSupportDownloads(scope, { page, pageSize: PLATFORM_PAGE_SIZE })
  const columns: Column<SupportDownload>[] = [
    { key: 'filename', header: 'Dosya', wrap: true, render: item => item.filename },
    { key: 'artifact', header: 'Artifact', wrap: true, render: item => <button className="text-primary hover:underline" onClick={() => update({ tab: 'browser', view: 'artifacts', artifact: item.artifactId })}>{item.artifactId}</button> },
    { key: 'state', header: 'Durum', render: item => <StatusPill status={item.state} /> },
    { key: 'bytes', header: 'Aktarım / Boyut', render: item => `${artifactSize(item.transferredBytes)} / ${artifactSize(item.sizeBytes)}` },
    { key: 'failure', header: 'Sonuç', wrap: true, render: item => item.failureCode ? failureMessage(item.failureCode) : '—' },
    { key: 'created', header: 'Başlangıç', render: item => artifactDate(item.createdAt) },
    { key: 'released', header: 'Bitiş', render: item => artifactDate(item.releasedAt) },
  ]
  if (!scope.enabled) return <p role="status">Download geçmişi için etkin oturum sahipliği ve ViewBackupMetadata veya DownloadBackup izni gerekir.</p>
  return <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4"><DataTable rows={query.data.items} columns={columns} keyExtractor={item => item.downloadId} /><Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ downloadPage: next })} /></div>}</SupportQueryState>
}
