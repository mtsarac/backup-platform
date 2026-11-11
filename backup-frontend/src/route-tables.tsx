import { Route } from 'react-router-dom'
import { NotFound } from './components/layout/not-found'
import { DashboardPage } from './pages/dashboard/dashboard-page'
import { ClientsPage } from './pages/clients/clients-page'
import { ClientDetailPage } from './pages/clients/client-detail-page'
import { AgentRegisterPage } from './pages/clients/agent-register-page'
import { BackupCenterLayout } from './pages/backup-center/backup-center-layout'
import { BackupJobsPage } from './pages/backup-jobs/backup-jobs-page'
import { BackupJobWizardPage } from './pages/backup-jobs/backup-job-wizard-page'
import { BackupJobEditPage } from './pages/backup-jobs/backup-job-edit-page'
import { BackupsPage } from './pages/backups/backups-page'
import { BackupDetailPage } from './pages/backups/backup-detail-page'
import { DiscoveryPage } from './pages/discovery/discovery-page'
import { InstanceDetailPage } from './pages/discovery/instance-detail-page'
import { UploadBandwidthPage } from './pages/upload-bandwidth/upload-bandwidth-page'
import { StorageLayout } from './pages/storage/storage-layout'
import { StorageUsageTab } from './pages/storage/storage-usage-tab'
import { StorageProvidersTab } from './pages/storage/storage-providers-tab'
import { ConnectionList } from './features/storage-connections/connection-list'
import { ConnectionDetail } from './features/storage-connections/connection-detail'
import { ConnectionWizard } from './features/storage-connections/connection-wizard'
import { ArtifactsPage } from './pages/artifacts/artifacts-page'
import { ArtifactDetailPage } from './pages/artifacts/artifact-detail-page'
import { RestoresPage } from './pages/restores/restores-page'
import { RestoreDetailPage } from './pages/restores/restore-detail-page'
import { RestoreWizardPage } from './pages/restores/restore-wizard-page'
import { SharesPage } from './pages/shares/shares-page'
import { PlatformDashboardPage } from './pages/platform/platform-dashboard-page'
import { PlatformPlansPage } from './pages/platform/platform-plans-page'
import { PlatformStoragePage } from './pages/platform/platform-storage-page'
import { PlatformStorageConnectionsPage } from './pages/platform/platform-storage-connections-page'
import { PlatformStorageLayout, PlatformStorageConnectionsRedirect } from './pages/platform/platform-storage-layout'
import { PlatformQuotaPoliciesPage } from './pages/platform/platform-quota-policies-page'
import { PlatformTenantsPage } from './pages/platform/platform-tenants-page'
import { PlatformTenantDetailPage } from './pages/platform/platform-tenant-detail-page'
import { PlatformUsersPage } from './pages/platform/platform-users-page'
import { PlatformAgentsPage } from './pages/platform/platform-agents-page'
import { PlatformAgentDetailPage } from './pages/platform/platform-agent-detail-page'
import { PlatformAuditPage } from './pages/platform/platform-audit-page'
import { NotificationsPage } from './pages/notifications/notifications-page'
import { PlatformNotificationTransportsPage } from './pages/platform/platform-notification-transports-page'
import { PlatformAgentSettingsPage } from './pages/platform/platform-agent-settings-page'
import { EncryptionPage } from './pages/encryption/encryption-page'

/** Routes under /tenants/:tenantId/*, relative to it. Kept as data so tests can check every menu link resolves to a real page. */
export const tenantRoutes = <>
  <Route index element={<DashboardPage />} />
  <Route path="agents" element={<ClientsPage />} />
  <Route path="agents/:id" element={<ClientDetailPage />} />
  <Route path="enrollment" element={<AgentRegisterPage />} />
  <Route element={<BackupCenterLayout />}>
    <Route path="backup-jobs" element={<BackupJobsPage />} />
    <Route path="backup-runs" element={<BackupsPage />} />
    <Route path="artifacts" element={<ArtifactsPage />} />
    <Route path="restores" element={<RestoresPage />} />
  </Route>
  <Route path="backup-jobs/new" element={<BackupJobWizardPage />} />
  <Route path="backup-jobs/:id/edit" element={<BackupJobEditPage />} />
  <Route path="backup-runs/:id" element={<BackupDetailPage />} />
  <Route path="storage" element={<StorageLayout />}>
    <Route index element={<ConnectionList />} />
    <Route path="usage" element={<StorageUsageTab />} />
    <Route path="providers" element={<StorageProvidersTab />} />
  </Route>
  <Route path="storage/connections/new" element={<ConnectionWizard />} />
  <Route path="storage/connections/:connectionId" element={<ConnectionDetail />} />
  <Route path="discovery" element={<DiscoveryPage />} />
  <Route path="discovery/instances/:instanceId" element={<InstanceDetailPage />} />
  <Route path="upload-bandwidth" element={<UploadBandwidthPage />} />
  <Route path="artifacts/:id" element={<ArtifactDetailPage />} />
  <Route path="artifacts/:id/restore" element={<RestoreWizardPage />} />
  <Route path="restores/:id" element={<RestoreDetailPage />} />
  <Route path="notifications" element={<NotificationsPage />} />
  <Route path="encryption" element={<EncryptionPage />} />
  <Route path="shares" element={<SharesPage />} />
  <Route path="*" element={<NotFound />} />
</>

/** Routes under /platform (inside PlatformShell), relative to it. */
export const platformRoutes = <>
  <Route index element={<PlatformDashboardPage />} />
  <Route path="tenants" element={<PlatformTenantsPage />} />
  <Route path="tenants/:tenantId" element={<PlatformTenantDetailPage />} />
  <Route path="tenants/:tenantId/agents/:agentId" element={<PlatformAgentDetailPage />} />
  <Route path="plans" element={<PlatformPlansPage />} />
  <Route path="storage" element={<PlatformStorageLayout />}>
    <Route index element={<PlatformStoragePage />} />
    <Route path="connections" element={<PlatformStorageConnectionsPage />} />
  </Route>
  <Route path="storage-connections" element={<PlatformStorageConnectionsRedirect />} />
  <Route path="quota-policies" element={<PlatformQuotaPoliciesPage />} />
  <Route path="users" element={<PlatformUsersPage />} />
  <Route path="agents" element={<PlatformAgentsPage />} />
  <Route path="audit" element={<PlatformAuditPage />} />
  <Route path="notification-transports" element={<PlatformNotificationTransportsPage />} />
  <Route path="agent-settings" element={<PlatformAgentSettingsPage />} />
  <Route path="*" element={<NotFound />} />
</>
