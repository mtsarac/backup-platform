import { tx, useLanguage } from '@/lib/i18n'
import { Outlet } from 'react-router-dom'
import { Badge } from '@/components/ui/badge'
import { PlatformSidebar } from './platform-sidebar'
import { ShellFrame } from './shell-frame'
import { ShellBar } from './top-bar'

function PlatformTopBar({ onMenuToggle }: { onMenuToggle: () => void }) {
  useLanguage()
  return <ShellBar onMenuToggle={onMenuToggle} context={<span className="text-sm font-medium text-text-heading">{tx("Platform yönetimi")}</span>} badges={<Badge tone="warning">{tx("PlatformAdmin")}</Badge>} />
}

/** Layout route for /platform/*: never touches tenant context (no tenant selector, no tenant nav). */
export function PlatformShell() {
  return <ShellFrame Sidebar={PlatformSidebar} TopBar={PlatformTopBar}><Outlet /></ShellFrame>
}
