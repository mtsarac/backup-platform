import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './components/auth/protected-route'
import { AppShell } from './components/layout/app-shell'
import { QueryState } from './components/data/query-state'
import { useAuth } from './features/auth/use-auth'
import { TenantProvider } from './features/tenancy/tenant-provider'
import { LoginPage } from './pages/login/login-page'
import { DashboardPage } from './pages/dashboard/dashboard-page'
import { ClientsPage } from './pages/clients/clients-page'
import { ClientDetailPage } from './pages/clients/client-detail-page'
import { AgentRegisterPage } from './pages/clients/agent-register-page'
import { BackupJobsPage } from './pages/backup-jobs/backup-jobs-page'
import { BackupJobWizardPage } from './pages/backup-jobs/backup-job-wizard-page'
import { BackupsPage } from './pages/backups/backups-page'
import { BackupDetailPage } from './pages/backups/backup-detail-page'
import { ArtifactsPage } from './pages/artifacts/artifacts-page'
import { ArtifactDetailPage } from './pages/artifacts/artifact-detail-page'
import { TenantSelectPage } from './pages/tenancy/tenant-select-page'
import { Button } from './components/ui/button'

function AppRoutes() {
  const { isAuthenticated, isLoading, error, refresh } = useAuth()
  if (isLoading) return <div className="mx-auto max-w-lg p-6"><QueryState isPending error={null} /></div>
  if (error) return <div className="mx-auto max-w-lg space-y-4 p-6"><QueryState isPending={false} error={error} /><Button onClick={refresh}>Tekrar dene</Button></div>
  return <Routes>
    <Route path="/login" element={isAuthenticated ? <Navigate to="/" replace /> : <LoginPage />} />
    <Route element={<ProtectedRoute isAuthenticated={isAuthenticated} />}>
      <Route path="/" element={<TenantSelectPage />} />
      <Route path="/tenants/:tenantId/*" element={<TenantProvider><AppShell><Routes>
        <Route index element={<DashboardPage />} />
        <Route path="agents" element={<ClientsPage />} />
        <Route path="agents/:id" element={<ClientDetailPage />} />
        <Route path="enrollment" element={<AgentRegisterPage />} />
        <Route path="backup-jobs" element={<BackupJobsPage />} />
        <Route path="backup-jobs/new" element={<BackupJobWizardPage />} />
        <Route path="backup-jobs/:id/edit" element={<BackupJobWizardPage />} />
        <Route path="backup-runs" element={<BackupsPage />} />
        <Route path="backup-runs/:id" element={<BackupDetailPage />} />
        <Route path="artifacts" element={<ArtifactsPage />} />
        <Route path="artifacts/:id" element={<ArtifactDetailPage />} />
        <Route path="*" element={<p>Sayfa bulunamadı.</p>} />
      </Routes></AppShell></TenantProvider>} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Route>
  </Routes>
}
export default function App() { return <BrowserRouter><AppRoutes /></BrowserRouter> }
