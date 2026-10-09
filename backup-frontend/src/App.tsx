import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './components/auth/protected-route'
import { PlatformAdminRoute } from './components/auth/platform-admin-route'
import { AppShell } from './components/layout/app-shell'
import { PlatformShell } from './components/layout/platform-shell'
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
import { StoragePage } from './pages/storage/storage-page'
import { ArtifactsPage } from './pages/artifacts/artifacts-page'
import { ArtifactDetailPage } from './pages/artifacts/artifact-detail-page'
import { RestoresPage } from './pages/restores/restores-page'
import { RestoreDetailPage } from './pages/restores/restore-detail-page'
import { RestoreWizardPage } from './pages/restores/restore-wizard-page'
import { TenantSelectPage } from './pages/tenancy/tenant-select-page'
import { Button } from './components/ui/button'
import { PlatformDashboardPage } from './pages/platform/platform-dashboard-page'
import { PlatformPlansPage } from './pages/platform/platform-plans-page'
import { PlatformStoragePage } from './pages/platform/platform-storage-page'
import { PlatformQuotaPoliciesPage } from './pages/platform/platform-quota-policies-page'
import { PlatformTenantsPage } from './pages/platform/platform-tenants-page'
import { PlatformTenantDetailPage } from './pages/platform/platform-tenant-detail-page'
import { PlatformUsersPage } from './pages/platform/platform-users-page'
import { PlatformAgentsPage } from './pages/platform/platform-agents-page'
import { PlatformAgentDetailPage } from './pages/platform/platform-agent-detail-page'
import { PlatformAuditPage } from './pages/platform/platform-audit-page'
import { PlatformSupportSessionsPage } from './pages/platform/platform-support-sessions-page'
import { PlatformSupportSessionPage } from './pages/platform/platform-support-session-page'
import { SupportAccessPage } from './pages/support-access/support-access-page'

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
        <Route path="storage" element={<StoragePage />} />
        <Route path="artifacts" element={<ArtifactsPage />} />
        <Route path="artifacts/:id" element={<ArtifactDetailPage />} />
        <Route path="artifacts/:id/restore" element={<RestoreWizardPage />} />
        <Route path="restores" element={<RestoresPage />} />
        <Route path="restores/:id" element={<RestoreDetailPage />} />
        <Route path="support-access" element={<SupportAccessPage />} />
        <Route path="*" element={<p>Sayfa bulunamadı.</p>} />
      </Routes></AppShell></TenantProvider>} />
      <Route path="/platform" element={<PlatformAdminRoute />}>
        <Route element={<PlatformShell />}>
          <Route index element={<PlatformDashboardPage />} />
          <Route path="tenants" element={<PlatformTenantsPage />} />
          <Route path="tenants/:tenantId" element={<PlatformTenantDetailPage />} />
          <Route path="tenants/:tenantId/agents/:agentId" element={<PlatformAgentDetailPage />} />
          <Route path="plans" element={<PlatformPlansPage />} />
          <Route path="storage" element={<PlatformStoragePage />} />
          <Route path="quota-policies" element={<PlatformQuotaPoliciesPage />} />
          <Route path="users" element={<PlatformUsersPage />} />
          <Route path="agents" element={<PlatformAgentsPage />} />
          <Route path="audit" element={<PlatformAuditPage />} />
          <Route path="support-sessions" element={<PlatformSupportSessionsPage />} />
          <Route path="support-sessions/:sessionId" element={<PlatformSupportSessionPage />} />
          <Route path="*" element={<p>Sayfa bulunamadı.</p>} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Route>
  </Routes>
}
export default function App() { return <BrowserRouter><AppRoutes /></BrowserRouter> }
