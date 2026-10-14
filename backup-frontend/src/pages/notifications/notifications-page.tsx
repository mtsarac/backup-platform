import { useState } from 'react'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Tabs } from '@/components/ui/tabs'
import { ChannelsSection } from '@/features/notifications/channels-section'
import { DeliveriesSection } from '@/features/notifications/deliveries-section'
import { PolicySection } from '@/features/notifications/policy-section'
import { useTenant } from '@/features/tenancy/use-tenant'

type Tab = 'policy' | 'channels' | 'deliveries'

/** Owner/Admin only (the API enforces it too); other roles get a notice and no request is made. */
export function NotificationsPage() {
  const { tenant, canAdmin } = useTenant()
  const [tab, setTab] = useState<Tab>('policy')
  if (!canAdmin) return <div className="space-y-6">
    <PageHeader title="Bildirimler" />
    <Notice tone="error" title="Bildirimleri yönetme yetkiniz yok">Bildirim ayarlarını yalnızca bu tenant'ın Owner ve Admin rolleri görebilir.</Notice>
  </div>
  return <div className="space-y-6">
    <PageHeader title="Bildirimler" description={`${tenant.name} · E-posta, webhook ve SMS ile uyarı bildirimleri.`} />
    <Tabs label="Bildirim bölümleri" idPrefix="notifications" value={tab} onChange={setTab} items={[{ value: 'policy', label: 'Politika' }, { value: 'channels', label: 'Kanallar' }, { value: 'deliveries', label: 'Gönderim geçmişi' }]} />
    <div role="tabpanel" id="notifications-panel-policy" aria-labelledby="notifications-tab-policy" hidden={tab !== 'policy'}>
      <PolicySection />
    </div>
    <div role="tabpanel" id="notifications-panel-channels" aria-labelledby="notifications-tab-channels" hidden={tab !== 'channels'}>
      <ChannelsSection />
    </div>
    <div role="tabpanel" id="notifications-panel-deliveries" aria-labelledby="notifications-tab-deliveries" hidden={tab !== 'deliveries'}>
      <DeliveriesSection />
    </div>
  </div>
}
