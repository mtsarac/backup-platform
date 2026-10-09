import { Outlet } from 'react-router-dom'
import { PlatformAccessDenied } from '@/components/auth/platform-access-denied'
import { useAuth } from '@/features/auth/use-auth'

/** UX guard only: the backend authorizes every /api/platform request with the PlatformAdmin policy. */
export function PlatformAdminRoute() {
  const { user } = useAuth()
  if (!user?.isPlatformAdmin) return <PlatformAccessDenied standalone />
  return <Outlet />
}
