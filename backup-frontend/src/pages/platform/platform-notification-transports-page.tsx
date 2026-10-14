import { useNotificationTransports } from '@/features/platform/notifications/hooks'
import { EmailTransportCard, SmsTransportCard } from '@/features/platform/notifications/transport-forms'
import { PlatformQueryState } from '@/features/platform/platform-query-state'

export function PlatformNotificationTransportsPage() {
  const query = useNotificationTransports()
  return <div className="space-y-6">
    <div><h1 className="text-2xl font-semibold text-text-heading">Bildirim altyapısı</h1>
      <p className="mt-1 text-sm text-text-muted">Tenant bildirimlerinin kullandığı platform genelindeki SMTP ve SMS ağ geçidi ayarları. Secret değerleri hiçbir zaman gösterilmez.</p></div>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {query.data && <>
        <EmailTransportCard transport={query.data.email} />
        <SmsTransportCard transport={query.data.sms} />
      </>}
    </PlatformQueryState>
  </div>
}
