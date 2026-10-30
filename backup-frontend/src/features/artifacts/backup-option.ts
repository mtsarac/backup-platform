import { tx } from '@/lib/i18n'
import type { PickerOption } from '@/components/ui/searchable-picker'
import type { BackupArtifact } from '@/features/artifacts/types'
import { formatDateTime } from '@/lib/format'

/** A backup is named by its database and the day it was taken; the job name is a second searchable line. */
export function backupOption(artifact: Pick<BackupArtifact, 'id' | 'databaseName' | 'createdAt' | 'jobName'>): PickerOption {
  return {
    value: artifact.id,
    label: `${artifact.databaseName || tx('Yedek dosyası')} · ${formatDateTime(artifact.createdAt)}`,
    description: artifact.jobName || undefined,
  }
}
