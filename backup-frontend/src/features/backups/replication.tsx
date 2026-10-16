import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import type { ReplicaRepairOperation, ReplicationDestination, ReplicationHealth, ReplicationStatus } from '@/features/artifacts/types'
import { failureMessage } from '@/lib/api/failure-messages'

type Tone = 'success' | 'warning' | 'error'

const statusMeta: Record<ReplicationStatus, { label: string; tone: Tone }> = {
  Healthy: { get label() { return tx("Replikasyon sağlıklı") }, tone: 'success' },
  Degraded: { get label() { return tx("Kısmi replikasyon · politika karşılandı") }, tone: 'warning' },
  InsufficientReplicas: { get label() { return tx("Yetersiz doğrulanmış replika") }, tone: 'error' },
  Unavailable: { get label() { return tx("Doğrulanmış replika yok") }, tone: 'error' },
}

const destinationStates: Record<string, { label: string; tone: Tone | 'neutral' }> = {
  Verified: { get label() { return tx("Doğrulandı") }, tone: 'success' },
  Failed: { get label() { return tx("Başarısız") }, tone: 'error' },
  Cancelled: { get label() { return tx("İptal edildi") }, tone: 'neutral' },
  NotAttempted: { get label() { return tx("Denenmedi") }, tone: 'neutral' },
  Deleted: { get label() { return tx("Silindi") }, tone: 'neutral' },
}

const providerLabels: Record<string, string> = { get local() { return tx("Agent yerel") }, get central() { return tx("Merkezi depolama") }, sftp: 'SFTP', 'google-drive': 'Google Drive' }

/** The status is always the backend's verdict; nothing here recomputes it from the counts. */
export function ReplicationBadge({ replication }: { replication?: ReplicationHealth | null }) {
  useLanguage()
  if (!replication) return null
  const meta = statusMeta[replication.status] ?? { label: replication.status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{tx(meta.label)}</Badge>
}

/** "2 / 2 doğrulanmış replika gerekli · 3 hedef": verified copies against the policy's requirement, plus how many destinations the job has. */
function replicationCounts(replication: ReplicationHealth): string {
  return tx("{0} / {1} doğrulanmış replika gerekli · {2} hedef", { "0": replication.verifiedReplicas, "1": replication.requiredVerifiedReplicas, "2": replication.destinationCount })
}

export function ReplicationSummary({ replication }: { replication?: ReplicationHealth | null }) {
  useLanguage()
  if (!replication) return null
  return <span className="inline-flex flex-wrap items-center gap-2"><ReplicationBadge replication={replication} /><span className="text-xs text-text-muted">{replicationCounts(replication)}</span></span>
}

function DestinationRow({
  destination,
  onRepair,
  isRepairing,
  canRepair,
  repairOperation
}: {
  destination: ReplicationDestination
  onRepair?: (destination: ReplicationDestination) => void
  isRepairing?: boolean
  canRepair?: boolean
  repairOperation?: ReplicaRepairOperation | null
}) {
  useLanguage()
  const meta = destinationStates[destination.state] ?? { label: destination.state, tone: 'neutral' as const }
  const isAutoPending = repairOperation?.status === 'Pending' && Boolean(repairOperation?.nextRetryAt)
  const isExhausted = Boolean(repairOperation?.isExhausted)
  const repairEligible = Boolean(canRepair && destination.state !== 'Verified' && onRepair)
  return (
    <li className="flex flex-wrap items-start justify-between gap-2 rounded-lg border border-border bg-surface-2 p-3 text-sm">
      <div className="min-w-0 space-y-1">
        <p className="font-medium text-text">{providerLabels[destination.storageProvider] ?? destination.storageProvider}{destination.isPrimary && <span className="ml-2 text-xs text-text-muted">{tx("Birincil")}</span>}</p>
        {destination.state === 'Failed' && destination.failureCode && <p className="text-error">{failureMessage(destination.failureCode)} <span className="text-text-muted">· {destination.failureCode}</span></p>}
        {destination.objectMayRemain && destination.replicaStatus !== 'Quarantined' && <p className="text-warning">{tx("Hedefte tamamlanmamış bir dosya kalmış olabilir; saklama süreci bu dosyayı da kaldırır.")}</p>}
        {destination.replicaStatus === 'Quarantined' && <p className="text-warning">{tx("Bu replika karantinaya alınmıştır; sahipliği doğrulanmadan otomatik veya manuel olarak silinmez.")}</p>}
        {isAutoPending && (
          <p className="text-sm font-medium text-warning">{tx("Otomatik yeniden deneme bekleniyor (Deneme")}{' '}{repairOperation?.attemptCount ?? 0} / {repairOperation?.maxAttempts ?? 5})
            {repairOperation?.nextRetryAt && tx(" · Sonraki: {0}", { "0": new Date(repairOperation.nextRetryAt).toLocaleTimeString(currentLocale()) })}
            {repairOperation?.lastFailureCode && ` (${repairOperation.lastFailureCode})`}
          </p>
        )}
        {isExhausted && (
          <p className="text-sm font-medium text-error">{tx("Yeniden deneme sınırına ulaşıldı (")}{repairOperation?.attemptCount ?? 5} / {repairOperation?.maxAttempts ?? 5})
            {repairOperation?.lastFailureCode && tx(" · Son hata: {0}", { "0": repairOperation.lastFailureCode })}
          </p>
        )}
      </div>
      <div className="flex items-center gap-2">
        <Badge tone={meta.tone}>{tx(meta.label)}</Badge>
        {repairEligible && (
          <Button
            size="sm"
            variant="outline"
            disabled={isRepairing}
            onClick={() => onRepair!(destination)}
          >
            {isRepairing ? tx('Onarılıyor…') : tx('Onar')}
          </Button>
        )}
      </div>
    </li>
  )
}

/** A failed destination is shown next to the verified ones: a partial failure is never folded into one "success". */
export function ReplicationSection({
  replication,
  onRepair,
  repairingDestinationId,
  canRepair,
  repairs
}: {
  replication?: ReplicationHealth | null
  onRepair?: (destination: ReplicationDestination) => void
  repairingDestinationId?: string | null
  canRepair?: boolean
  repairs?: ReplicaRepairOperation[] | null
}) {
  useLanguage()
  if (!replication) return null
  return (
    <Section title={tx("Replikasyon")} description={replicationCounts(replication)} actions={<ReplicationBadge replication={replication} />}>
      <div className="space-y-3">
      {replication.status === 'Degraded' && <Notice tone="warning">{tx("Yedek kullanılabilir ve politika karşılanıyor; mevcut doğrulanmış replikalardan indirme ve geri yükleme yapılabilir.")}</Notice>}
      {replication.status === 'InsufficientReplicas' && <Notice tone="error">{tx("Politikanın gerektirdiği replika sayısına ulaşılamadı; ancak mevcut doğrulanmış replikalardan indirme ve geri yükleme mümkündür.")}</Notice>}
      {replication.status === 'Unavailable' && <Notice tone="error">{tx("Hiçbir hedefte doğrulanmış kopya yok; indirme ve geri yükleme kullanılamaz.")}</Notice>}
      {replication.destinations.length > 0 && (
        <ul className="space-y-2" aria-label={tx("Hedef sonuçları")}>
          {replication.destinations.map((destination) => {
            const destId = destination.storageConnectionId ?? destination.storageProvider
            const destRepairs = repairs?.filter(r => destination.storageConnectionId ? r.storageConnectionId === destination.storageConnectionId : r.storageProvider === destination.storageProvider)
            const repairOp = destRepairs && destRepairs.length > 0 ? destRepairs[0] : null
            const isRepairing = repairingDestinationId === destId || (destination.storageConnectionId !== null && repairingDestinationId === destination.storageConnectionId)
            return (
              <DestinationRow
                key={`${destination.storageProvider}:${destination.storageConnectionId ?? 'default'}`}
                destination={destination}
                onRepair={onRepair}
                isRepairing={isRepairing}
                canRepair={canRepair}
                repairOperation={repairOp}
              />
            )
          })}
        </ul>
      )}
      </div>
    </Section>
  )
}
