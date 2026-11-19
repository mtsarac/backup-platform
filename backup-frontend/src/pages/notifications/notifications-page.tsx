import { tx, useLanguage } from '@/lib/i18n'
import { Tabs } from '@/components/ui/tabs'
import { ChannelsSection } from '@/features/notifications/channels-section'
import { DeliveriesSection } from '@/features/notifications/deliveries-section'
import { PolicySection } from '@/features/notifications/policy-section'
import { useTenant } from '@/features/tenancy/use-tenant'
import { oneOf, useListParams } from '@/features/platform/use-list-params'
import { isFeatureEnabled } from '@/features/feature-flags/feature-flags'

const tabs = ['policy', 'channels', 'deliveries'] as const

export function NotificationsPage() {
  useLanguage()
  const { tenant } = useTenant()
  const { get, update } = useListParams()
  const tab = oneOf(get('tab'), tabs) ?? 'policy'
  return <div className="space-y-6">
    <p className="text-body text-text-muted">{tx(isFeatureEnabled('sms') ? "{0} · E-posta, webhook ve SMS ile uyarı bildirimleri." : "{0} · E-posta ve webhook ile uyarı bildirimleri.", { "0": tenant.name })}</p>
    <Tabs label={tx("Bildirim bölümleri")} idPrefix="notifications" value={tab} onChange={value => update({ tab: value === 'policy' ? '' : value, status: '', kind: '', eventType: '' })} items={[{ value: 'policy', label: tx('Politika') }, { value: 'channels', label: tx('Kanallar') }, { value: 'deliveries', label: tx('Gönderim geçmişi') }]} />
    <div role="tabpanel" id={`notifications-panel-${tab}`} aria-labelledby={`notifications-tab-${tab}`}>
      {tab === 'policy' && <PolicySection />}
      {tab === 'channels' && <ChannelsSection />}
      {tab === 'deliveries' && <DeliveriesSection />}
    </div>
  </div>
}
