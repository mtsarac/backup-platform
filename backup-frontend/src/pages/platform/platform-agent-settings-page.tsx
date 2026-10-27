import { tx, useLanguage } from '@/lib/i18n'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { useAgentSettings } from '@/features/platform/agent-settings/hooks'
import { AgentSettingsForm } from '@/features/platform/agent-settings/agent-settings-form'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

export function PlatformAgentSettingsPage() {
  useLanguage()
  const query = useAgentSettings()
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Yedekleme bilgisayarı ayarları")} description={tx("Yedekleme bilgisayarlarının bağlanacağı sunucu adresi ve kurulum anahtarlarının süresi burada bir kez belirlenir.")} />
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {query.data && <AgentSettingsForm settings={query.data} />}
    </QueryState>
  </div>
}
