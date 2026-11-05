import { Outlet } from 'react-router-dom'
import { AccessDenied } from '@/components/auth/access-denied'
import { platformOnlyMessage } from '@/lib/api/failure-messages'
import { useAuth } from '@/features/auth/use-auth'

/** UX guard only: the backend authorizes every /api/platform request with the PlatformAdmin policy. */
export function PlatformAdminRoute() {
  const { user } = useAuth()
  if (!user?.isPlatformAdmin) return <AccessDenied standalone what={platformOnlyMessage()} />
  return <Outlet />
}
