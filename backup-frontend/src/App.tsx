import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './components/auth/protected-route'
import { AppShell } from './components/layout/app-shell'
import { useAuth } from './features/auth/use-auth'
import { LoginPage } from './pages/login/login-page'
import { DashboardPage } from './pages/dashboard/dashboard-page'
import { ClientsPage } from './pages/clients/clients-page'
import { ClientDetailPage } from './pages/clients/client-detail-page'
import { AgentRegisterPage } from './pages/clients/agent-register-page'
import { BackupJobsPage } from './pages/backup-jobs/backup-jobs-page'
import { BackupJobWizardPage } from './pages/backup-jobs/backup-job-wizard-page'
import { BackupsPage } from './pages/backups/backups-page'
import { BackupDetailPage } from './pages/backups/backup-detail-page'
import { RestorePage } from './pages/restore/restore-page'
import { StoragePage } from './pages/storage/storage-page'
import { StorageFormPage } from './pages/storage/storage-form-page'
import { LogsPage } from './pages/logs/logs-page'
import { UsersPage } from './pages/users/users-page'
import { SettingsPage } from './pages/settings/settings-page'
import { isAdministrator } from './lib/ownership'
import { ROUTES } from './lib/routes'

function AppRoutes() {
  const { isAuthenticated, user } = useAuth()

  return (
    <Routes>
      <Route path={ROUTES.LOGIN} element={isAuthenticated ? <Navigate to={ROUTES.DASHBOARD} replace /> : <LoginPage />} />
      <Route element={<ProtectedRoute isAuthenticated={isAuthenticated} />}>
        <Route
          path="*"
          element={
            <AppShell>
              <Routes>
                <Route path={ROUTES.DASHBOARD} element={<DashboardPage />} />
                <Route path={ROUTES.CLIENTS} element={<ClientsPage />} />
                <Route path={ROUTES.CLIENT_DETAIL} element={<ClientDetailPage />} />
                <Route path={ROUTES.CLIENT_REGISTER} element={<AgentRegisterPage />} />
                <Route path={ROUTES.BACKUP_JOBS} element={<BackupJobsPage />} />
                <Route path={ROUTES.BACKUP_JOB_NEW} element={<BackupJobWizardPage />} />
                <Route path={ROUTES.BACKUP_JOB_EDIT} element={<BackupJobWizardPage />} />
                <Route path={ROUTES.BACKUP_HISTORY} element={<BackupsPage />} />
                <Route path={ROUTES.BACKUP_DETAIL} element={<BackupDetailPage />} />
                <Route path={ROUTES.RESTORE} element={<RestorePage />} />
                <Route path={ROUTES.STORAGE} element={<StoragePage />} />
                <Route path={ROUTES.LOGS} element={<LogsPage />} />
                <Route
                  path={ROUTES.USERS}
                  element={isAdministrator(user) ? <UsersPage /> : <Navigate to={ROUTES.DASHBOARD} replace />}
                />
                <Route
                  path={ROUTES.SETTINGS}
                  element={isAdministrator(user) ? <SettingsPage /> : <Navigate to={ROUTES.DASHBOARD} replace />}
                />
                <Route
                  path={ROUTES.STORAGE_NEW}
                  element={
                    isAdministrator(user) ? <StorageFormPage /> : <Navigate to={ROUTES.STORAGE} replace />
                  }
                />
                <Route path="*" element={<div className="text-text-muted">Sayfa Bulunamadı</div>} />
              </Routes>
            </AppShell>
          }
        />
      </Route>
    </Routes>
  )
}

function App() {
  return (
    <BrowserRouter>
      <AppRoutes />
    </BrowserRouter>
  )
}

export default App
