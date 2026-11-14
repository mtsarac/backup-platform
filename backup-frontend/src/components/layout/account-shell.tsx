import { ArrowLeft, ShieldCheck } from 'lucide-react'
import { Outlet } from 'react-router-dom'
import { useAuth } from '@/features/auth/use-auth'
import { tx, useLanguage } from '@/lib/i18n'
import { AccountLink } from './account-link'
import { SidebarFrame, type NavGroup } from './nav'
import { ShellFrame } from './shell-frame'
import { ShellBar } from './top-bar'

function AccountSidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  useLanguage()
  const { user } = useAuth()
  const groups: NavGroup[] = [{ items: [
    { label: tx("Çalışma alanlarına dön"), to: '/', icon: ArrowLeft, end: true },
    ...(user?.role === 'PlatformAdmin' ? [{ label: tx("Platform Yönetimi"), to: '/platform', icon: ShieldCheck }] : []),
  ] }]
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel={tx("Ana menü")} label={tx("Ana gezinme")} brand={{ icon: ShieldCheck, title: tx("Yedekleme Platformu"), caption: tx("Hesap") }} groups={groups} footer={<AccountLink />} />
}

function AccountTopBar({ onMenuToggle }: { onMenuToggle: () => void }) {
  useLanguage()
  return <ShellBar onMenuToggle={onMenuToggle} context={<span className="text-sm font-medium text-text-heading">{tx("Ayarlar")}</span>} />
}

/** Layout route for /account/*: the account belongs to the user, not to a workspace, so it has its own small shell (no tenant context) with the same pinned settings entry. */
export function AccountShell() {
  return <ShellFrame Sidebar={AccountSidebar} TopBar={AccountTopBar}><Outlet /></ShellFrame>
}
