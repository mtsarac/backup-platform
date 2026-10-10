import { Badge } from '@/components/ui/badge'
import type { ReplicationDestination, ReplicationHealth, ReplicationStatus } from '@/features/artifacts/types'
import { failureMessage } from '@/lib/api/failure-messages'

type Tone = 'success' | 'warning' | 'error'

const statusMeta: Record<ReplicationStatus, { label: string; tone: Tone }> = {
  Healthy: { label: 'Replikasyon sağlıklı', tone: 'success' },
  Degraded: { label: 'Kısmi replikasyon · politika karşılandı', tone: 'warning' },
  InsufficientReplicas: { label: 'Yetersiz doğrulanmış replika', tone: 'error' },
  Unavailable: { label: 'Doğrulanmış replika yok', tone: 'error' },
}

const destinationStates: Record<string, { label: string; tone: Tone | 'neutral' }> = {
  Verified: { label: 'Doğrulandı', tone: 'success' },
  Failed: { label: 'Başarısız', tone: 'error' },
  Cancelled: { label: 'İptal edildi', tone: 'neutral' },
  NotAttempted: { label: 'Denenmedi', tone: 'neutral' },
  Deleted: { label: 'Silindi', tone: 'neutral' },
}

const providerLabels: Record<string, string> = { local: 'Agent yerel', central: 'Merkezi depolama', sftp: 'SFTP', 'google-drive': 'Google Drive' }

/** The status is always the backend's verdict; nothing here recomputes it from the counts. */
export function ReplicationBadge({ replication }: { replication?: ReplicationHealth | null }) {
  if (!replication) return null
  const meta = statusMeta[replication.status] ?? { label: replication.status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{meta.label}</Badge>
}

/** "2 / 2 doğrulanmış replika gerekli · 3 hedef": verified copies against the policy's requirement, plus how many destinations the job has. */
function replicationCounts(replication: ReplicationHealth): string {
  return `${replication.verifiedReplicas} / ${replication.requiredVerifiedReplicas} doğrulanmış replika gerekli · ${replication.destinationCount} hedef`
}

export function ReplicationSummary({ replication }: { replication?: ReplicationHealth | null }) {
  if (!replication) return null
  return <span className="inline-flex flex-wrap items-center gap-2"><ReplicationBadge replication={replication} /><span className="text-xs text-text-muted">{replicationCounts(replication)}</span></span>
}

function DestinationRow({ destination }: { destination: ReplicationDestination }) {
  const meta = destinationStates[destination.state] ?? { label: destination.state, tone: 'neutral' as const }
  return (
    <li className="flex flex-wrap items-start justify-between gap-2 rounded-md border border-border bg-surface-2 p-3 text-sm">
      <div className="space-y-1">
        <p className="font-medium text-text">{providerLabels[destination.storageProvider] ?? destination.storageProvider}{destination.isPrimary && <span className="ml-2 text-xs text-text-muted">Birincil</span>}</p>
        {destination.state === 'Failed' && destination.failureCode && <p className="text-error">{destination.failureReason ?? failureMessage(destination.failureCode)} <span className="text-text-muted">· {destination.failureCode}</span></p>}
        {destination.objectMayRemain && <p className="text-warning">Hedefte tamamlanmamış bir dosya kalmış olabilir; saklama süreci bu dosyayı da kaldırır.</p>}
      </div>
      <Badge tone={meta.tone}>{meta.label}</Badge>
    </li>
  )
}

/** A failed destination is shown next to the verified ones: a partial failure is never folded into one "success". */
export function ReplicationSection({ replication }: { replication?: ReplicationHealth | null }) {
  if (!replication) return null
  return (
    <section aria-label="Replikasyon" className="space-y-3 rounded-lg border border-border bg-surface p-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold text-text-heading">Replikasyon</h2>
        <ReplicationBadge replication={replication} />
      </div>
      <p className="text-sm text-text-muted">{replicationCounts(replication)}</p>
      {replication.status === 'Degraded' && <p role="status" className="text-sm text-warning">Yedek kullanılabilir ve politika karşılanıyor; ancak bazı hedeflerde doğrulanmış kopya yok.</p>}
      {replication.status === 'InsufficientReplicas' && <p role="alert" className="text-sm text-error">Yedek kullanılabilir, ancak politikanın gerektirdiği doğrulanmış replika sayısına ulaşılamadı.</p>}
      {replication.status === 'Unavailable' && <p role="alert" className="text-sm text-error">Hiçbir hedefte doğrulanmış kopya yok; kullanılabilir bir yedek oluşmadı.</p>}
      {replication.destinations.length > 0 && <ul className="space-y-2" aria-label="Hedef sonuçları">{replication.destinations.map((destination) => <DestinationRow key={`${destination.storageProvider}:${destination.storageConnectionId ?? 'default'}`} destination={destination} />)}</ul>}
    </section>
  )
}
