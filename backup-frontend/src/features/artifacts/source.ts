import { tx } from '@/lib/i18n'
import type { BackupArtifact } from './types'

type SourceFields = Pick<BackupArtifact, 'databaseName' | 'agentName' | 'backupMetadata'>

/** What a file backup covered inside its source: "reports, docs", or the whole source for "."; null when the response does not say. */
export function artifactSourceScope(artifact: Pick<BackupArtifact, 'sourcePaths'>): string | null {
  const paths = artifact.sourcePaths
  if (!paths || paths.length === 0) return null
  return paths.every(path => path === '.') ? tx('Tüm kaynak') : paths.join(', ')
}

/** The kind of source from the engine the backup reported; null when the response does not say, never a guess. */
export function artifactSourceType(artifact: Pick<BackupArtifact, 'backupMetadata'>): string | null {
  switch (artifact.backupMetadata?.engine) {
    case 'mssql': return tx('MSSQL')
    case 'postgres': return tx('PostgreSQL')
    case 'file-system': return tx('Dosya yedeği')
    default: return null
  }
}

/** Source type, source name (database, or the file source reference) and computer, each only when the API returned it. */
export function artifactSource(artifact: SourceFields) {
  return { type: artifactSourceType(artifact), name: artifact.databaseName || null, agent: artifact.agentName || null }
}

/** "MSSQL · Accounting · SQL Agent" with missing parts left out. */
export function artifactSourceLabel(artifact: SourceFields): string {
  const { type, name, agent } = artifactSource(artifact)
  return [type, name, agent].filter(Boolean).join(' · ')
}
