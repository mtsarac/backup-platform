import { tx, useLanguage } from '@/lib/i18n'
import { createBrowserRouter, Navigate, Route, RouterProvider, Routes } from 'react-router-dom'
import { ProtectedRoute } from './components/auth/protected-route'
import { PlatformAdminRoute } from './components/auth/platform-admin-route'
import { AppShell } from './components/layout/app-shell'
import { PlatformShell } from './components/layout/platform-shell'
import { QueryState } from './components/data/query-state'
import { useAuth } from './features/auth/use-auth'
import { TenantProvider } from './features/tenancy/tenant-provider'
import { LoginPage } from './pages/login/login-page'
import { TenantSelectPage } from './pages/tenancy/tenant-select-page'
import { SessionsPage } from './pages/login/sessions-page'
import { SecurityPage } from './pages/login/security-page'
import { Button } from './components/ui/button'
import { platformRoutes, tenantRoutes } from './route-tables'

function AppRoutes() {
  useLanguage()
  const { isAuthenticated, isLoading, error, refresh } = useAuth()
  if (isLoading) return <div className="mx-auto max-w-lg p-6"><QueryState isPending error={null} /></div>
  if (error) return <div className="mx-auto max-w-lg space-y-4 p-6"><QueryState isPending={false} error={error} /><Button onClick={refresh}>{tx("Tekrar dene")}</Button></div>
  return <Routes>
    <Route path="/login" element={isAuthenticated ? <Navigate to="/" replace /> : <LoginPage />} />
    <Route element={<ProtectedRoute isAuthenticated={isAuthenticated} />}>
      <Route path="/" element={<TenantSelectPage />} />
      <Route path="/sessions" element={<SessionsPage />} />
      <Route path="/security" element={<SecurityPage />} />
      <Route path="/tenants/:tenantId/*" element={<TenantProvider><AppShell><Routes>{tenantRoutes}</Routes></AppShell></TenantProvider>} />
      <Route path="/platform" element={<PlatformAdminRoute />}>
        <Route element={<PlatformShell />}>{platformRoutes}</Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Route>
  </Routes>
}
const router = createBrowserRouter([{ path: '*', element: <AppRoutes /> }])

export default function App() {
  useLanguage()
  return <RouterProvider router={router} /> }
