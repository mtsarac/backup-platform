import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { ROUTES } from '@/lib/routes'

export function ProtectedRoute({ isAuthenticated }: { isAuthenticated: boolean }) {
  const location = useLocation()
  if (!isAuthenticated) {
    return <Navigate to={ROUTES.LOGIN} state={{ from: location }} replace />
  }
  return <Outlet />
}
