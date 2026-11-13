import { Settings } from 'lucide-react'
import { Link, useLocation } from 'react-router-dom'
import { useAuth } from '@/features/auth/use-auth'
import { tx, useLanguage } from '@/lib/i18n'

/** Pinned at the bottom of every sidebar: the signed-in user's name and the way into the account settings. */
export function AccountLink() {
  useLanguage()
  const { user } = useAuth()
  const active = useLocation().pathname.startsWith('/account')
  return <Link to="/account" aria-current={active ? 'page' : undefined}
    className={`flex min-h-11 items-center gap-3 rounded-md px-3 py-1.5 text-body transition-colors ${active ? 'bg-sidebar-active text-text-heading' : 'text-sidebar-foreground hover:bg-sidebar-active/60'}`}>
    <Settings aria-hidden="true" className="size-4 shrink-0 text-sidebar-muted" />
    <span className="min-w-0 leading-tight"><span className="block truncate font-medium" title={user?.displayName}>{user?.displayName}</span><span className="block text-xs text-sidebar-muted">{tx('Ayarlar')}</span></span>
  </Link>
}
