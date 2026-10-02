import { Bell, Menu, Moon, Search, Sun } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { useTheme } from '@/hooks/use-theme'
import { isAdministrator } from '@/lib/ownership'

interface TopBarProps {
  onMenuToggle: () => void
}

export function TopBar({ onMenuToggle }: TopBarProps) {
  const { logout, user } = useAuth()
  const { resolvedTheme, toggleTheme } = useTheme()

  return (
    <header className="flex h-14 min-w-0 items-center justify-between border-b border-border bg-surface px-6">
      <div className="flex items-center gap-3">
        <Button variant="ghost" size="icon" className="lg:hidden" onClick={onMenuToggle} aria-label="Menüyü aç">
          <Menu className="size-5" />
        </Button>
        <span className="text-sm font-medium text-text-heading">Backup Yönetimi</span>
      </div>
      <div className="flex min-w-0 items-center gap-3">
        <div className="relative hidden min-w-0 md:block">
          <Search className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-text-muted" />
          <input
            type="search"
            placeholder="Ara..."
            className="h-8 w-full min-w-0 rounded-md border border-border bg-surface-2 pl-9 pr-3 text-sm outline-none focus:border-ring"
          />
        </div>
        <button className="relative rounded-md p-2 text-text-muted hover:bg-surface-2" aria-label="Bildirimler">
          <Bell className="size-4" />
        </button>
        <Button variant="ghost" size="icon" onClick={toggleTheme} aria-label="Tema değiştir">
          {resolvedTheme === 'light' ? <Moon className="size-4" /> : <Sun className="size-4" />}
        </Button>
        <div className="flex min-w-0 items-center gap-2">
          <Badge tone={isAdministrator(user) ? 'info' : 'neutral'}>
            {isAdministrator(user) ? 'Admin' : 'User'}
          </Badge>
          <span className="hidden text-sm text-text-muted sm:inline">{user?.username}</span>
          <Button variant="ghost" size="sm" onClick={logout}>
            Çıkış
          </Button>
        </div>
      </div>
    </header>
  )
}
