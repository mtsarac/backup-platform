import { Activity, Bell, Database, DatabaseBackup, Gauge, HardDrive, LayoutDashboard, Monitor, Radar, RotateCcw, ShieldCheck, Upload } from 'lucide-react'
import { useAuth } from '@/features/auth/use-auth'
import { useTenant } from '@/features/tenancy/use-tenant'
import { SidebarFrame, type NavGroup } from './nav'
import { useTranslation } from '@/lib/i18n'

export function Sidebar({ mobileOpen, onMobileClose }: { mobileOpen?: boolean; onMobileClose?: () => void }) {
  const { tenantId, canAdmin } = useTenant()
  const { user } = useAuth()
  const { t } = useTranslation()
  const base = `/tenants/${tenantId}`
  const groups: NavGroup[] = [
    { items: [{ label: t('nav.dashboard'), to: base, icon: LayoutDashboard, end: true }] },
    { heading: t('nav.backupSection'), items: [
      { label: t('nav.backupJobs'), to: `${base}/backup-jobs`, icon: Database },
      { label: t('nav.backupRuns'), to: `${base}/backup-runs`, icon: Activity },
      { label: t('nav.artifacts'), to: `${base}/artifacts`, icon: HardDrive },
      { label: t('nav.restores'), to: `${base}/restores`, icon: RotateCcw },
    ] },
    { heading: t('nav.infraSection'), items: [
      { label: t('nav.agents'), to: `${base}/agents`, icon: Monitor },
      { label: t('nav.discovery'), to: `${base}/discovery`, icon: Radar },
      { label: t('nav.storage'), to: `${base}/storage`, icon: Gauge },
      { label: t('nav.bandwidth'), to: `${base}/upload-bandwidth`, icon: Upload },
    ] },
    ...(canAdmin ? [{ heading: t('nav.workspaceSection'), items: [
      { label: t('nav.notifications'), to: `${base}/notifications`, icon: Bell },
      { label: t('nav.supportAccess'), to: `${base}/support-access`, icon: ShieldCheck },
    ] }] : []),
    ...(user?.isPlatformAdmin ? [{ heading: t('nav.platformSection'), items: [{ label: t('nav.platformAdmin'), to: '/platform', icon: ShieldCheck }] }] : []),
  ]
  return <SidebarFrame mobileOpen={mobileOpen} onMobileClose={onMobileClose} dialogLabel={t('nav.dialogLabel')} label={t('nav.label')} brand={{ icon: DatabaseBackup, title: t('nav.brandTitle'), caption: t('nav.brandCaption') }} groups={groups} />
}
