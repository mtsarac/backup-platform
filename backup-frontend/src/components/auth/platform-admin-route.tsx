import { Outlet } from 'react-router-dom'
import { AccessDenied, platformOnlyMessage } from '@/components/auth/access-denied'
import { useAuth } from '@/features/auth/use-auth'

/** UX guard only: the backend authorizes every /api/platform request with the PlatformAdmin policy. */
export function PlatformAdminRoute() {
  const { user } = useAuth()
  if (!user?.isPlatformAdmin) return <AccessDenied standalone what={platformOnlyMessage()} />
  return <Outlet />
}
