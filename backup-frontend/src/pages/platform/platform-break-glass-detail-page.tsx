import { tx, useLanguage } from '@/lib/i18n'
import { useParams } from 'react-router-dom'
import { BreakGlassDetail } from '@/features/artifact-encryption/break-glass-detail'
import { useBreakGlassRequest } from '@/features/artifact-encryption/hooks'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'

export function PlatformBreakGlassDetailPage() {
  useLanguage()
  const { requestId = '' } = useParams()
  const query = useBreakGlassRequest(requestId)
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb, { label: tx('Acil destek erişimi'), to: platformPaths.breakGlass }]} title={tx("Acil destek erişimi isteği")} />
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <BreakGlassDetail key={query.data.id} request={query.data} receivedAt={query.dataUpdatedAt} />}
    </PlatformQueryState>
  </div>
}
