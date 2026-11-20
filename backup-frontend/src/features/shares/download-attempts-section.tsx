import { Disclosure } from '@/components/ui/disclosure'
import { useListState } from '@/features/lists/use-list-state'
import { tx, useLanguage } from '@/lib/i18n'
import { downloadAttemptsListConfig } from './download-attempts-config'
import { DownloadAttemptsList } from './download-attempts-list'
import type { DownloadAttemptScope } from './types'

/** The download history under the shares: collapsed, and open while its own search, filter, sort or page is in the URL. */
export function DownloadAttemptsSection({ scope, description }: { scope: DownloadAttemptScope; description: string }) {
  useLanguage()
  const state = useListState(downloadAttemptsListConfig)
  return <Disclosure title={tx('İndirme girişimleri')} open={state.customized}>
    <div className="space-y-3">
      <p className="text-sm text-text-muted">{description}</p>
      <DownloadAttemptsList scope={scope} />
    </div>
  </Disclosure>
}
