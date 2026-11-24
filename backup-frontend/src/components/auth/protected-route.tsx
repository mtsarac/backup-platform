import { useContext } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { AuthContext } from '@/features/auth/auth-context'
import { ROUTES } from '@/lib/routes'

export function ProtectedRoute({ isAuthenticated }: { isAuthenticated: boolean }) {
  const location = useLocation()
  const auth = useContext(AuthContext)
  if (!isAuthenticated) {
    return <Navigate to={ROUTES.LOGIN} state={auth?.loggedOut ? undefined : { from: location }} replace />
  }
  return <Outlet />
}
