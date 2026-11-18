import { tx, useLanguage } from '@/lib/i18n'
import { Outlet } from 'react-router-dom'
import { PageHeader } from '@/components/ui/page-header'
import { RouteTabs } from '@/components/ui/tabs'
import { useTenant } from '@/features/tenancy/use-tenant'

/** One heading and the three settings sections as route tabs; notifications and the recovery key are administrator-only, so others see only upload bandwidth. */
export function SettingsLayout() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const base = `/tenants/${tenantId}/settings`
  return <div className="space-y-6">
    <PageHeader title={tx("Ayarlar")} />
    <RouteTabs label={tx("Ayar bölümleri")} items={[
      ...(canAdmin ? [{ to: base, label: tx('Bildirimler'), end: true }, { to: `${base}/recovery-key`, label: tx('Kurtarma anahtarı') }] : []),
      { to: `${base}/bandwidth`, label: tx('Yükleme bant genişliği') },
    ]} />
    <Outlet />
  </div>
}
