import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { agentDate } from '@/features/agents/format'
import { formatCapacity, formatExactBytes } from '@/features/storage/format'
import { ownershipLabel } from '@/features/storage/labels'
import { capabilityLabel, availabilityMeta, connectionHealth, ownershipTone, secretStatusLabel, testMessage, testStatusMeta } from './labels'
import type { ConnectionOwnership, ConnectionTest, ConnectionView, ProviderAvailability } from './types'

export function OwnershipBadge({ ownership }: { ownership: ConnectionOwnership }) {
  useLanguage()
  return <Badge tone={ownershipTone[ownership]}>{ownershipLabel(ownership)}</Badge>
}

export function EnabledBadge({ enabled }: { enabled: boolean }) {
  useLanguage()
  return <Badge tone={enabled ? 'success' : 'warning'}>{enabled ? tx('Etkin') : tx('Devre dışı')}</Badge>
}

export function HealthBadge({ connection }: { connection: Parameters<typeof connectionHealth>[0] }) {
  useLanguage()
  const health = connectionHealth(connection)
  return <Badge tone={health.tone} dot>{tx(health.label)}</Badge>
}

export function ConnectionState({ connection }: { connection: Pick<ConnectionView, 'enabled' | 'effectiveEnabled' | 'administrativelyDisabled'> }) {
  useLanguage()
  return <div className="space-y-1">
    <EnabledBadge enabled={connection.effectiveEnabled} />
    <span className="block text-xs text-text-muted">{tx("Sahip tercihi:")}{' '}{connection.enabled ? tx('Etkin') : tx('Devre dışı')}</span>
    {connection.administrativelyDisabled && <Badge tone="warning">{tx("Platform kilidi")}</Badge>}
  </div>
}

export function AvailabilityBadge({ availability }: { availability: ProviderAvailability }) {
  useLanguage()
  const meta = availabilityMeta[availability]
  return <Badge tone={meta.tone}>{tx(meta.label)}</Badge>
}

export function CapabilityChips({ capabilities, label = tx('Yetenekler') }: { capabilities: readonly string[]; label?: string }) {
  useLanguage()
  if (capabilities.length === 0) return <span className="text-xs text-text-muted">{tx("Bildirilen yetenek yok")}</span>
  return <ul aria-label={label} className="flex flex-wrap gap-1">
    {capabilities.map((capability) => <li key={capability}><Badge tone="neutral" className="font-mono text-[11px]"><span title={capability}>{capabilityLabel(capability)}</span></Badge></li>)}
  </ul>
}

/** The result exactly as the backend reported it: NotTestable is a warning, never success, and the message is shown as returned. */
export function LastTest({ test }: { test: ConnectionTest | null }) {
  useLanguage()
  if (!test) return <span className="text-text-muted">{tx("Henüz test edilmedi")}</span>
  const meta = Object.hasOwn(testStatusMeta, test.status) ? testStatusMeta[test.status] : { label: String(test.status), tone: 'neutral' as const }
  return <div className="space-y-0.5">
    <div className="flex flex-wrap items-center gap-1.5"><Badge tone={meta.tone}>{tx(meta.label)}</Badge><span className="text-xs text-text-muted">{agentDate(test.testedAt)}</span></div>
    {(test.code || test.message) && <p className="whitespace-normal break-words text-xs text-text-muted">{testMessage(test)}</p>}
  </div>
}

export function SecretStatus({ connection }: { connection: Pick<ConnectionView, 'hasSecret' | 'secretUpdatedAt' | 'providerType' | 'configuration'> }) {
  useLanguage()
  if (connection.providerType === 'google-drive') {
    const reauth = connection.configuration?.reauthorizationRequired === 'true' || !connection.hasSecret
    return <span className="inline-flex flex-wrap items-center gap-1.5">
      <Badge tone={reauth ? 'warning' : 'success'}>{reauth ? tx('Yeniden yetkilendirme gerekli') : tx('Bağlı')}</Badge>
      {connection.configuration?.accountEmail && <span className="text-xs text-text-muted">{String(connection.configuration.accountEmail)}</span>}
    </span>
  }
  return <span className="inline-flex flex-wrap items-center gap-1.5">
    <Badge tone={connection.hasSecret ? 'success' : 'neutral'}>{secretStatusLabel(connection)}</Badge>
    {connection.hasSecret && connection.secretUpdatedAt && <span className="text-xs text-text-muted">{agentDate(connection.secretUpdatedAt)}</span>}
  </span>
}

export function Footprint({ connection }: { connection: Pick<ConnectionView, 'replicaCount' | 'storedBytes'> }) {
  useLanguage()
  return <span className="inline-flex flex-col">
    <span>{connection.replicaCount.toLocaleString(currentLocale())}{' '}{tx("Replica")}</span>
    <span className="text-xs text-text-muted" title={formatExactBytes(connection.storedBytes)}>{formatCapacity(connection.storedBytes)}</span>
  </span>
}
