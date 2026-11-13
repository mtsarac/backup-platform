import { tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { artifactAvailability } from './availability'
import type { BackupArtifact } from './types'

/** Icon + text status of a backup file, with the reason as small supporting text. */
export function ArtifactAvailability({ artifact }: { artifact: BackupArtifact }) {
  useLanguage()
  const { level, label, reason } = artifactAvailability(artifact)
  const downloadable = artifact.state === 'Available'
  return (
    <div className="space-y-1">
      <div className="flex flex-wrap items-center gap-1.5">
        <StatusPill level={level}>{label}</StatusPill>
        {downloadable && <span className="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium bg-surface-2 text-text-muted border border-border/50">{tx("İndirilebilir")}</span>}
      </div>
      {reason && <span className="block break-words text-xs text-text-muted">{reason}</span>}
    </div>
  )
}
