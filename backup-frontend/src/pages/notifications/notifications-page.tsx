import { ShieldAlert } from 'lucide-react'
import { ChannelsSection } from '@/features/notifications/channels-section'
import { DeliveriesSection } from '@/features/notifications/deliveries-section'
import { PolicySection } from '@/features/notifications/policy-section'
import { useTenant } from '@/features/tenancy/use-tenant'

/** Owner/Admin only (the API enforces it too); other roles get a notice and no request is made. */
export function NotificationsPage() {
  const { tenant, canAdmin } = useTenant()
  if (!canAdmin) return <div role="alert" className="space-y-2 rounded-lg border border-error/30 bg-error-subtle p-6 text-error">
    <div className="flex items-center gap-2"><ShieldAlert className="size-5" aria-hidden="true" /><h1 className="font-medium">Bildirimleri yönetme yetkiniz yok</h1></div>
    <p className="text-sm">Bildirim ayarlarını yalnızca bu tenant'ın Owner ve Admin rolleri görebilir.</p>
  </div>
  return <div className="space-y-6">
    <div><h1 className="text-2xl font-semibold text-text-heading">Bildirimler</h1><p className="mt-1 text-sm text-text-muted">{tenant.name} · E-posta, webhook ve SMS ile uyarı bildirimleri.</p></div>
    <PolicySection />
    <ChannelsSection />
    <DeliveriesSection />
  </div>
}
