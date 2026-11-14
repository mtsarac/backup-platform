import { Outlet } from 'react-router-dom'
import { RouteTabs } from '@/components/ui/tabs'
import { tx, useLanguage } from '@/lib/i18n'

/** Sub-navigation for the account settings; each tab is the existing page, which keeps its own heading. */
export function AccountLayout() {
  useLanguage()
  return <div className="max-w-3xl space-y-6">
    <RouteTabs label={tx("Hesap ayarları")} items={[
      { to: '/account', label: tx('Hesap'), end: true },
      { to: '/account/sessions', label: tx('Oturumlarım') },
      { to: '/account/security', label: tx('Güvenlik') },
      { to: '/account/passkeys', label: tx('Geçiş anahtarları') },
    ]} />
    <Outlet />
  </div>
}
