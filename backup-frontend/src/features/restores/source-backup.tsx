import { tx } from '@/lib/i18n'
import { useArtifact } from '@/features/artifacts/hooks'
import { useBackup } from '@/features/backups/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatDateTime } from '@/lib/format'
import type { RestoreView } from './types'

/**
 * The database the restored backup was taken from. A PostgreSQL restore view and its artifact may omit the name, so the
 * run's configuration (already cached for the backup pages) is the last place to look.
 */
export function useSourceDatabaseName(restore: RestoreView | undefined) {
  const { tenantId } = useTenant()
  const artifact = useArtifact(tenantId, restore?.artifactId ?? '')
  const run = useBackup(artifact.data?.runId ?? '')
  const name = restore?.sourceDatabaseName ?? restore?.result?.sourceBackup?.databaseName ?? artifact.data?.databaseName ?? run.data?.configuration.database
  return { name: name || undefined, createdAt: restore?.artifactCreatedAt ?? artifact.data?.createdAt }
}

/** How a restore is titled: a verification trial is named after the backup's database, never after its temporary `bp_verify_…` copy. */
export function restoreTitle(restore: RestoreView, sourceName: string | undefined): string {
  if (restore.purpose !== 'Verification') return restore.databaseName
  return sourceName ? tx('Doğrulama denemesi: {0}', { '0': sourceName }) : tx('Doğrulama denemesi')
}

/** Names the source backup so that rows of one database stay distinguishable: the database and the day it was backed up. */
export function sourceBackupLabel(name: string | undefined, createdAt: string | null | undefined): string {
  if (name && createdAt) return `${name} · ${formatDateTime(createdAt)}`
  return name ?? (createdAt ? tx('Yedek dosyası · {0}', { '0': formatDateTime(createdAt) }) : tx('Yedek dosyasını aç'))
}
