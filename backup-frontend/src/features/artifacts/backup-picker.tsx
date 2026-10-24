import { tx, useLanguage } from '@/lib/i18n'
import { ErrorNotice } from '@/components/ui/notice'
import { SearchablePicker, type PickerOption } from '@/components/ui/searchable-picker'
import { useArtifact, useArtifacts } from '@/features/artifacts/hooks'
import type { BackupArtifact } from '@/features/artifacts/types'
import { formatDateTime } from '@/lib/format'

// shortcut: the list endpoint has no name search, so only the 100 newest backups are searchable; move the search to the endpoint when tenants outgrow that.
const loaded = { page: 1, pageSize: 100, state: '', from: '', to: '' } as const

/** A backup is named by its database and the day it was taken; the job name is a second searchable line. */
function backupOption(artifact: BackupArtifact): PickerOption {
  return {
    value: artifact.id,
    label: `${artifact.databaseName || tx('Yedek dosyası')} · ${formatDateTime(artifact.createdAt)}`,
    description: artifact.jobName || undefined,
  }
}

/** Picks a backup file (artifact) by typing its database or job name, so nobody has to paste an id. */
export function BackupPicker({ tenantId, value, onChange }: { tenantId: string; value: string; onChange: (id: string) => void }) {
  useLanguage()
  const list = useArtifacts(tenantId, loaded)
  const options = (list.data?.items ?? []).map(backupOption)
  // A chosen backup older than the loaded ones is named from its own record.
  const outside = useArtifact(tenantId, value && list.data && !options.some(option => option.value === value) ? value : '')
  return <div className="space-y-2">
    <SearchablePicker label={tx("Yedek dosyası")} placeholder={tx("Tüm yedek dosyaları")} options={options} value={value} onChange={onChange}
      selectedLabel={outside.data ? backupOption(outside.data).label : undefined} disabled={list.isPending} />
    {list.error && <ErrorNotice title={tx("Yedek dosyaları yüklenemedi")} error={list.error} />}
  </div>
}
