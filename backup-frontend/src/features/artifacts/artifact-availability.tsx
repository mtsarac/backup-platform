import { useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { artifactAvailability } from './availability'
import type { BackupArtifact } from './types'

/** Icon + text status of a backup file, with the reason as small supporting text. */
export function ArtifactAvailability({ artifact }: { artifact: BackupArtifact }) {
  useLanguage()
  const { level, label, reason } = artifactAvailability(artifact)
  return <span className="block max-w-64 space-y-0.5"><StatusPill level={level}>{label}</StatusPill>{reason && <span className="block break-words text-xs text-text-muted">{reason}</span>}</span>
}
