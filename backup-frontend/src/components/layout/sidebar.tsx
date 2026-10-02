import { Activity, Database, HardDrive, LayoutDashboard, Monitor, Server, Settings, Users, X } from 'lucide-react'
import { NavLink } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/features/auth/use-auth'
import { isAdministrator } from '@/lib/ownership'
import { ROUTES } from '@/lib/routes'

const groups = [
  {
    label: 'Genel',
    items: [{ label: 'Panel', to: ROUTES.DASHBOARD, icon: LayoutDashboard }],
  },
  {
    label: 'Yönetim',
    items: [
      { label: "Client'lar", to: ROUTES.CLIENTS, icon: Monitor },
      { label: 'Yedekleme İşleri', to: ROUTES.BACKUP_JOBS, icon: Database },
      { label: 'Depolama', to: ROUTES.STORAGE, icon: Server },
    ],
  },
  {
    label: 'Geçmiş',
    items: [
      { label: 'Yedekleme Geçmişi', to: ROUTES.BACKUP_HISTORY, icon: HardDrive },
      { label: 'Loglar', to: ROUTES.LOGS, icon: Activity },
    ],
  },
  {
    label: 'Admin',
    items: [
      { label: 'Kullanıcılar', to: ROUTES.USERS, icon: Users },
      { label: 'Ayarlar', to: ROUTES.SETTINGS, icon: Settings },
    ],
  },
]

interface SidebarProps {
  mobileOpen?: boolean
  onMobileClose?: () => void
}

function SidebarContent({ onItemClick }: { onItemClick?: () => void }) {
  const { user } = useAuth()
  const admin = isAdministrator(user)
  const visibleGroups = admin
    ? groups
    : groups.map((group) =>
        group.label === 'Admin' ? { ...group, items: [] } : group,
      )
  return (
    <>
      <div className="flex h-14 items-center justify-between border-b border-border px-4 lg:justify-start lg:gap-2">
        <div className="flex items-center gap-2">
          <div className="flex size-7 items-center justify-center rounded bg-primary text-primary-foreground">
            <HardDrive className="size-4" />
          </div>
          <span className="font-semibold text-text-heading">Backup Yönetimi</span>
        </div>
        {onItemClick && (
          <Button variant="ghost" size="icon" className="lg:hidden" onClick={onItemClick} aria-label="Menüyü kapat">
            <X className="size-5" />
          </Button>
        )}
      </div>
      <nav className="flex-1 overflow-auto p-3">
        {visibleGroups.map((group) => (
          <div key={group.label} className="mb-4">
            <div className="mb-1 px-3 text-xs font-medium text-text-muted">{group.label}</div>
            <ul className="space-y-0.5">
              {group.items.map((item) => (
                <li key={item.label}>
                  <NavLink
                    to={item.to}
                    onClick={onItemClick}
                    className={({ isActive }) =>
                      `flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors ${
                        isActive
                          ? 'border-l-2 border-primary bg-accent/50 font-medium text-accent-foreground'
                          : 'text-text hover:bg-surface-2'
                      }`
                    }
                  >
                    <item.icon className="size-4" />
                    {item.label}
                  </NavLink>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </nav>
    </>
  )
}

export function Sidebar({ mobileOpen, onMobileClose }: SidebarProps) {
  return (
    <>
      <aside className="fixed inset-y-0 left-0 z-20 hidden w-60 flex-col border-r border-border bg-surface lg:flex">
        <SidebarContent />
      </aside>
      {mobileOpen && (
        <div
          className="fixed inset-0 z-10 bg-black/50 lg:hidden"
          onClick={onMobileClose}
          aria-hidden="true"
        />
      )}
      <aside
        className={`fixed inset-y-0 left-0 z-20 flex w-60 flex-col border-r border-border bg-surface transition-transform lg:hidden ${
          mobileOpen ? 'translate-x-0' : '-translate-x-full'
        }`}
      >
        <SidebarContent onItemClick={onMobileClose} />
      </aside>
    </>
  )
}
