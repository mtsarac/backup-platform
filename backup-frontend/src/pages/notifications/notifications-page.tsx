import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Tabs } from '@/components/ui/tabs'
import { ChannelsSection } from '@/features/notifications/channels-section'
import { DeliveriesSection } from '@/features/notifications/deliveries-section'
import { PolicySection } from '@/features/notifications/policy-section'
import { useTenant } from '@/features/tenancy/use-tenant'
import { accessDenied } from '@/lib/api/failure-messages'

type Tab = 'policy' | 'channels' | 'deliveries'

/** Owner/Admin only (the API enforces it too); other roles get a notice and no request is made. */
export function NotificationsPage() {
  useLanguage()
  const { tenant, canAdmin } = useTenant()
  const [tab, setTab] = useState<Tab>('policy')
  if (!canAdmin) return <div className="space-y-6">
    <PageHeader title={tx("Bildirimler")} />
    <ErrorNotice title={tx("Bildirimleri yönetme yetkiniz yok")} {...accessDenied(tx("Bildirim ayarlarını yalnızca bu tenant'ın Owner ve Admin rolleri görebilir."))} />
  </div>
  return <div className="space-y-6">
    <PageHeader title={tx("Bildirimler")} description={tx("{0} · E-posta, webhook ve SMS ile uyarı bildirimleri.", { "0": tenant.name })} />
    <Tabs label={tx("Bildirim bölümleri")} idPrefix="notifications" value={tab} onChange={setTab} items={[{ value: 'policy', label: tx('Politika') }, { value: 'channels', label: tx('Kanallar') }, { value: 'deliveries', label: tx('Gönderim geçmişi') }]} />
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
