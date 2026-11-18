import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useArtifact } from './hooks'

export function ArtifactRunLink({ tenantId, artifactId, className, children }: { tenantId: string; artifactId: string; className?: string; children: ReactNode }) {
  const artifact = useArtifact(tenantId, artifactId)
  return artifact.data?.runId
    ? <Link className={className} to={`/tenants/${tenantId}/backup-runs/${artifact.data.runId}`}>{children}</Link>
    : <span>{children}</span>
}
