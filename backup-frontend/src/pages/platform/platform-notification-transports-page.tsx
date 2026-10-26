import { tx, useLanguage } from '@/lib/i18n'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { useNotificationTransports } from '@/features/platform/notifications/hooks'
import { EmailTransportCard, SmsTransportCard } from '@/features/platform/notifications/transport-forms'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/components/auth/access-denied'

export function PlatformNotificationTransportsPage() {
  useLanguage()
  const query = useNotificationTransports()
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Bildirim altyapısı")} description={tx("Tenant bildirimlerinin kullandığı e-posta ve SMS gönderimi çalışıyor mu? Gizli değerler hiçbir zaman gösterilmez.")} />
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {query.data && <>
        <EmailTransportCard transport={query.data.email} />
        <SmsTransportCard transport={query.data.sms} />
      </>}
    </QueryState>
  </div>
}
