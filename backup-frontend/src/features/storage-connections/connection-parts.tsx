import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { StatusPill } from '@/components/data/status-pill'
import { capabilityLabel, connectionHealth, secretStatusLabel, platformLockState, testMessage } from './labels'
import type { ConnectionTest, ConnectionView } from './types'
import { formatBytes, formatDateTime } from '@/lib/format'

export function HealthBadge({ connection }: { connection: Parameters<typeof connectionHealth>[0] }) {
  useLanguage()
  const health = connectionHealth(connection)
  return <StatusPill level={health.level}>{health.label}</StatusPill>
}

/** Space running out in everyday words; nothing for a healthy or unmeasured destination. */
export function CapacityPill({ status }: { status: string | undefined }) {
  useLanguage()
  if (status === 'Critical') return <StatusPill level="urgent">{tx("Alan dolmak üzere")}</StatusPill>
  if (status === 'Warning') return <StatusPill level="attention">{tx("Alan azalıyor")}</StatusPill>
  return null
}

export function ConnectionState({ connection }: { connection: Pick<ConnectionView, 'enabled' | 'effectiveEnabled' | 'administrativelyDisabled'> }) {
  useLanguage()
  return <div className="space-y-1">
    <StatusPill status={connection.effectiveEnabled ? 'Active' : 'Disabled'} />
    <span className="block text-xs text-text-muted">{tx("Sahip tercihi:")}{' '}{connection.enabled ? tx('Etkin') : tx('Devre dışı')}</span>
    {connection.administrativelyDisabled && <>
      <StatusPill level="attention">{tx("Platform kilidi")}</StatusPill>
      <span className="block text-xs text-text-muted">{tx(platformLockState)}</span>
    </>}
  </div>
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
  return <div className="space-y-0.5">
    <div className="flex flex-wrap items-center gap-1.5"><StatusPill domain="connectionTest" status={String(test.status)} /><span className="text-xs text-text-muted">{formatDateTime(test.testedAt, tx('Henüz bildirilmedi'))}</span></div>
    {(test.code || test.message) && <p className="whitespace-normal break-words text-xs text-text-muted">{testMessage(test)}</p>}
  </div>
}

export function SecretStatus({ connection }: { connection: Pick<ConnectionView, 'hasSecret' | 'secretUpdatedAt' | 'providerType' | 'configuration'> }) {
  useLanguage()
  if (connection.providerType === 'google-drive') {
    const reauth = connection.configuration?.reauthorizationRequired === 'true' || !connection.hasSecret
    return <span className="inline-flex flex-wrap items-center gap-1.5">
      <StatusPill level={reauth ? 'attention' : 'safe'}>{reauth ? tx('Yeniden yetkilendirme gerekli') : tx('Bağlı')}</StatusPill>
      {connection.configuration?.accountEmail && <span className="text-xs text-text-muted">{String(connection.configuration.accountEmail)}</span>}
    </span>
  }
  return <span className="inline-flex flex-wrap items-center gap-1.5">
    <StatusPill level={connection.hasSecret ? 'safe' : 'neutral'}>{secretStatusLabel(connection)}</StatusPill>
    {connection.hasSecret && connection.secretUpdatedAt && <span className="text-xs text-text-muted">{formatDateTime(connection.secretUpdatedAt, tx('Henüz bildirilmedi'))}</span>}
  </span>
}

export function Footprint({ connection }: { connection: Pick<ConnectionView, 'replicaCount' | 'storedBytes'> }) {
  useLanguage()
  return <span className="inline-flex flex-col">
    <span>{connection.replicaCount.toLocaleString(currentLocale())}{' '}{tx("Replica")}</span>
    <span className="text-xs text-text-muted" title={formatBytes(connection.storedBytes)}>{formatBytes(connection.storedBytes)}</span>
  </span>
}
