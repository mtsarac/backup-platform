import { tx, useLanguage } from '@/lib/i18n'
import type { BackupConfiguration, BackupDestination } from '@/features/backup-jobs/types'
import { CheckField } from '@/components/ui/check-field'
import { Select, TextInput } from '@/components/ui/text-input'
import { ownershipLabel } from '@/features/storage/labels'
import { errorMessage } from '@/lib/api/failure-messages'
import type { ConnectionView, ProviderDescriptor } from './types'
import { connectionUnavailableReason, primaryDestination, resolveDestinationConnection } from './job-connection-picker-utils'
import { connectionDisplayName, executionLocationLabel } from './labels'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Disclosure } from '@/components/ui/disclosure'
import { actionName } from '@/lib/accessible-name'

interface Props {
  configuration: BackupConfiguration
  connections: readonly ConnectionView[]
  providers: readonly ProviderDescriptor[]
  pending: boolean
  error: unknown
  fieldError?: string
  replicaError?: string
  replicationError?: string
  existingTarget: boolean
  compact?: boolean
  onChange: (configuration: BackupConfiguration) => void
  onRefresh: () => void
}

function pickerOwnership(ownership: string) {
  return ownership === 'TenantManaged' ? '' : ownershipLabel(ownership)
}

/** The "no connection chosen" entry stands for the provider's default connection, so it carries that connection's plain name. */
function defaultOptionLabel(defaultConnection: ConnectionView | undefined, providerName: string, reason: string | null) {
  return defaultConnection
    ? [connectionDisplayName(defaultConnection), pickerOwnership(defaultConnection.ownership), reason ?? tx('Kullanılabilir')].filter(Boolean).join(' · ')
    : [tx('Varsayılan'), providerName, tx('Bağlantı yok'), reason ?? tx('Kullanılabilir')].join(' · ')
}

export function MultiDestinationPicker({ configuration, connections, providers, pending, error, fieldError, replicaError, replicationError, existingTarget, compact = false, onChange, onRefresh }: Props) {
  useLanguage()
  const destinations = configuration.destinations
  const primary = primaryDestination(configuration)
  const target = resolveDestinationConnection(primary, connections)
  const provider = providers.find(item => item.providerType === primary.storageProvider)
  const targetProvider = providers.find(item => item.providerType === target?.providerType)
  const reason = connectionUnavailableReason(target, targetProvider)
  const defaultTarget = connections.find(item => item.providerType === primary.storageProvider && item.isDefault)
  const defaultReason = connectionUnavailableReason(defaultTarget, provider)
  const selectedMissing = primary.storageConnectionId && !target
  const selectedConnectionId = target?.isDefault ? null : primary.storageConnectionId

  const requiredCount = configuration.requiredVerifiedReplicaCount
  function replacePrimaryTarget(next: Pick<BackupDestination, 'storageProvider' | 'storageConnectionId' | 'storageExecutionLocation'>) {
    onChange({ ...configuration, destinations: destinations.map(d => d === primary ? { ...d, ...next } : d) })
  }

  function chooseConnection(id: string) {
    if (!id) {
      if (!defaultReason && provider?.executionLocation) {
        replacePrimaryTarget({ storageProvider: primary.storageProvider, storageConnectionId: null, storageExecutionLocation: provider.executionLocation })
      }
      return
    }
    const connection = connections.find(item => item.id === id)
    const descriptor = providers.find(item => item.providerType === connection?.providerType)
    if (connection && descriptor?.executionLocation && !connectionUnavailableReason(connection, descriptor)) {
      replacePrimaryTarget({ storageProvider: connection.providerType, storageConnectionId: connection.id, storageExecutionLocation: descriptor.executionLocation })
    }
  }

  function updateDestinations(newDestinations: BackupDestination[], newRequiredCount?: number) {
    let nextCount = newRequiredCount ?? requiredCount
    if (nextCount > newDestinations.length) nextCount = newDestinations.length
    if (configuration.enforceThreeTwoOne) nextCount = Math.max(3, nextCount)
    if (nextCount < 1) nextCount = 1

    onChange({
      ...configuration,
      destinations: newDestinations,
      requiredVerifiedReplicaCount: nextCount
    })
  }

  function handleSetPrimary(index: number) {
    const updated = destinations.map((d, i) => ({
      ...d,
      isPrimary: i === index
    }))
    updateDestinations(updated)
  }

  function handleRemove(index: number) {
    if (destinations.length <= (configuration.enforceThreeTwoOne ? 3 : 1)) return
    const wasPrimary = destinations[index].isPrimary
    const remaining = destinations.filter((_, i) => i !== index)
    if (wasPrimary && remaining.length > 0) {
      remaining[0] = { ...remaining[0], isPrimary: true }
    }
    updateDestinations(remaining)
  }

  function handleAddDestination(providerType: string, connectionId: string | null) {
    const providerDesc = providers.find(p => p.providerType === providerType)
    if (!providerDesc || !providerDesc.executionLocation) return

    const exists = destinations.some(d =>
      d.storageProvider === providerType && (d.storageConnectionId ?? null) === (connectionId ?? null)
    )
    if (exists) return

    const newDest: BackupDestination = {
      storageProvider: providerType,
      storageConnectionId: connectionId,
      storageExecutionLocation: providerDesc.executionLocation,
      isPrimary: destinations.length === 0
    }

    updateDestinations([...destinations, newDest])
  }

  return (
    <>
      {compact ? <div className="space-y-1 md:col-span-2">
        <label htmlFor="primary-storage-destination" className="block text-sm font-medium text-text-heading">{tx('Depolama hedefi')}</label>
        <Select id="primary-storage-destination" value={JSON.stringify([primary.storageProvider, selectedConnectionId ?? null])} disabled={pending || Boolean(error)} aria-invalid={Boolean(fieldError)} aria-describedby={fieldError ? 'primary-storage-connection-error' : undefined} onChange={event => {
          const [providerType, connectionId] = JSON.parse(event.target.value) as [string, string | null]
          if (connectionId) {
            chooseConnection(connectionId)
          } else if (providerType === primary.storageProvider) {
            chooseConnection('')
          } else {
            const descriptor = providers.find(item => item.providerType === providerType)
            if (descriptor?.availability === 'Available' && descriptor.executionLocation && descriptor.topology) {
              replacePrimaryTarget({ storageProvider: providerType, storageConnectionId: null, storageExecutionLocation: descriptor.executionLocation })
            }
          }
        }}>
          {(!provider || selectedMissing || (target && target.providerType !== primary.storageProvider)) && <option value={JSON.stringify([primary.storageProvider, primary.storageConnectionId ?? null])} disabled>{primary.storageConnectionId ?? primary.storageProvider} · {tx('Bağlantı bulunamadı')}</option>}
          {providers.map(item => {
            const defaultConnection = connections.find(connection => connection.providerType === item.providerType && connection.isDefault)
            const unavailable = connectionUnavailableReason(defaultConnection, item)
            return <optgroup key={item.providerType} label={tx(item.displayName)}>
              <option value={JSON.stringify([item.providerType, null])} disabled={item.availability !== 'Available' || !item.executionLocation || !item.topology || (item.providerType === primary.storageProvider && Boolean(unavailable))}>{defaultOptionLabel(defaultConnection, tx(item.displayName), unavailable)}</option>
              {connections.filter(connection => connection.providerType === item.providerType && !connection.isDefault).map(connection => {
                const reason = connectionUnavailableReason(connection, item)
                return <option key={connection.id} value={JSON.stringify([item.providerType, connection.id])} disabled={Boolean(reason)}>{[connectionDisplayName(connection), tx(item.displayName), pickerOwnership(connection.ownership), reason ?? tx('Kullanılabilir')].filter(Boolean).join(' · ')}</option>
              })}
            </optgroup>
          })}
        </Select>
        {fieldError && <p id="primary-storage-connection-error" role="alert" className="text-sm text-error">{fieldError}</p>}
        <p className="text-xs text-text-muted">{tx("Varsayılan seçimi bu sağlayıcının tenant varsayılanına gider. Açık seçim bağlantı kimliğini korur. Son uygunluk kontrolünü sunucu yapar.")}</p>
      </div> : <>
      <div className="space-y-1">
        <label htmlFor="primary-storage-provider" className="block text-sm font-medium text-text-heading">{tx("Storage Provider")}</label>
        <Select id="primary-storage-provider" value={primary.storageProvider} disabled={pending || Boolean(error)} onChange={event => {
          const descriptor = providers.find(item => item.providerType === event.target.value)
          if (descriptor?.availability === 'Available' && descriptor.executionLocation && descriptor.topology) {
            replacePrimaryTarget({ storageProvider: descriptor.providerType, storageConnectionId: null, storageExecutionLocation: descriptor.executionLocation })
          }
        }}>
          {!provider && <option value={primary.storageProvider} disabled>{primary.storageProvider}{' '}{tx("· Sağlayıcı bulunamadı")}</option>}
          {providers.map(item => <option key={item.providerType} value={item.providerType} disabled={item.availability !== 'Available' || !item.executionLocation || !item.topology}>{tx(item.displayName)}{item.availability !== 'Available' ? tx(' · Yakında / kullanılamıyor') : ''}</option>)}
        </Select>
      </div>
      <div className="space-y-1">
        <label htmlFor="primary-storage-connection" className="block text-sm font-medium text-text-heading">{tx("Depolama bağlantısı")}</label>
        <Select id="primary-storage-connection" value={selectedConnectionId ?? ''} disabled={pending || Boolean(error)} aria-invalid={Boolean(fieldError)} aria-describedby="storage-connection-help primary-storage-connection-error" onChange={event => chooseConnection(event.target.value)}>
          <option value="" disabled={Boolean(defaultReason)}>{defaultOptionLabel(defaultTarget, tx(provider?.displayName ?? primary.storageProvider), defaultReason)}</option>
          {selectedMissing && <option value={primary.storageConnectionId!} disabled>{tx("Mevcut bağlantı ·")}{' '}{primary.storageConnectionId}{' '}{tx("· Bağlantı bulunamadı")}</option>}
          {connections.filter(connection => connection.providerType === primary.storageProvider && !connection.isDefault).map(connection => {
            const descriptor = providers.find(item => item.providerType === connection.providerType)
            const unavailable = connectionUnavailableReason(connection, descriptor)
            return <option key={connection.id} value={connection.id} disabled={Boolean(unavailable)}>{[connectionDisplayName(connection), tx(connection.providerDisplayName), pickerOwnership(connection.ownership), unavailable ?? tx('Kullanılabilir')].filter(Boolean).join(' · ')}</option>
          })}
        </Select>
        {fieldError && <p id="primary-storage-connection-error" role="alert" className="text-sm text-error">{fieldError}</p>}
        <p id="storage-connection-help" className="text-xs text-text-muted">{tx("Varsayılan seçimi bu sağlayıcının tenant varsayılanına gider. Açık seçim bağlantı kimliğini korur. Son uygunluk kontrolünü sunucu yapar.")}</p>
      </div>
      </>}
      {pending && <p role="status" className="text-sm">{tx("Depolama bağlantıları yükleniyor…")}</p>}
      {error ? <div role="alert" className="text-sm text-error">{errorMessage(error)} <Button variant="link" size="sm" type="button" onClick={onRefresh}>{tx("Bağlantıları yeniden yükle")}</Button></div> : !pending && reason && <p role="status" className="text-sm text-warning md:col-span-2">{target ? tx("“{0}” depolama hedefi", { "0": connectionDisplayName(target) }) : tx('Depolama hedefi')}: {reason}. {existingTarget ? tx("Mevcut hedef kimliği korunarak iş kaydedilebilir, ancak hedef kullanılabilir olana kadar yeni backup'lar (manuel ve zamanlanmış) başlatılamaz. Mevcut backup'lar etkilenmez.") : tx('Yeni iş veya yeni hedef için kullanılabilir bir bağlantı seçin; varsayılan seçimi bu engeli aşmaz.')}</p>}

      <Disclosure className="border-t border-border pt-4 md:col-span-2" open={Boolean(fieldError || replicaError || replicationError)} title={<>{tx("Başka hedef ekle")} <span className="text-xs font-normal">· {tx("Toplam")}{' '}{destinations.length}{' '}{tx("hedef")}</span></>}>
        <div className="space-y-3">
        <p className="text-xs text-text-muted">{tx("Bu yedekleme işinin birden fazla depolama konumuna kopyalanmasını yapılandırın.")}</p>
        {replicationError && <p id="replication-error" role="alert" className="text-sm text-error">{replicationError}</p>}

        <div className="space-y-2">
          {destinations.map((dest, index) => {
            const destConn = dest.storageConnectionId
              ? connections.find(c => c.id === dest.storageConnectionId)
              : connections.find(c => c.providerType === dest.storageProvider && c.isDefault)
            const destProv = providers.find(p => p.providerType === dest.storageProvider)
            const unavailable = connectionUnavailableReason(destConn, destProv)

            return (
              <div key={`${dest.storageProvider}:${dest.storageConnectionId ?? 'default'}`} className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 rounded-lg border border-border bg-surface p-2.5 text-sm">
                <div className="space-y-0.5">
                  <div className="flex items-center gap-2">
                    {!dest.isPrimary && <span className="font-medium text-text-heading">{tx(destProv?.displayName ?? dest.storageProvider)}</span>}
                    {dest.isPrimary ? (
                      <Badge tone="accent">{tx("Primary")}</Badge>
                    ) : (
                      <Badge>{tx("Replica")}</Badge>
                    )}
                    {!dest.isPrimary && unavailable && (
                      <Badge tone="error">
                        {unavailable}
                      </Badge>
                    )}
                  </div>
                  {!dest.isPrimary && <div className="text-xs text-text-muted">
                    {destConn ? `${connectionDisplayName(destConn)}${pickerOwnership(destConn.ownership) ? ` (${pickerOwnership(destConn.ownership)})` : ''}` : (dest.storageConnectionId ? dest.storageConnectionId : tx('Tenant Varsayılanı'))} · {executionLocationLabel(dest.storageExecutionLocation)}
                  </div>}
                </div>

                <div className="flex items-center gap-2 self-end sm:self-center">
                  {!dest.isPrimary && (
                    <Button variant="link" size="sm"
                      type="button"
                      onClick={() => handleSetPrimary(index)}
                    >{tx("Primary Yap")}</Button>
                  )}
                  {destinations.length > 1 && (
                    <Button variant="destructive" size="sm"
                      type="button"
                      aria-label={actionName(tx("Kaldır"), tx(destProv?.displayName ?? dest.storageProvider))}
                      disabled={Boolean(configuration.enforceThreeTwoOne) && destinations.length <= 3}
                      onClick={() => handleRemove(index)}
                    >{tx("Kaldır")}</Button>
                  )}
                </div>
              </div>
            )
          })}
        </div>

        <div className="grid gap-3 sm:grid-cols-2 pt-2">
          <div>
            <label htmlFor="add-replication-destination" className="block text-xs font-medium text-text-muted mb-1">{tx("Yeni Replikasyon Hedefi Ekle")}</label>
            <Select
              id="add-replication-destination"
             
              value=""
              disabled={pending || Boolean(error)}
              onChange={e => {
                const val = e.target.value
                if (!val) return
                if (val.startsWith('default:')) {
                  const pType = val.split(':')[1]
                  handleAddDestination(pType, null)
                } else {
                  const conn = connections.find(c => c.id === val)
                  if (conn) handleAddDestination(conn.providerType, conn.id)
                }
              }}
            >
              <option value="">{tx("+ Replikasyon hedefi seçin…")}</option>
              <optgroup label={tx("Varsayılan sağlayıcı hedefleri")}>
                {providers.filter(p => p.availability === 'Available' && p.executionLocation).map(p => {
                  const defaultConnection = connections.find(c => c.providerType === p.providerType && c.isDefault)
                  const alreadyAdded = destinations.some(d => d.storageProvider === p.providerType && (!d.storageConnectionId || d.storageConnectionId === defaultConnection?.id))
                  return (
                    <option key={`default:${p.providerType}`} value={`default:${p.providerType}`} disabled={alreadyAdded}>{defaultConnection ? connectionDisplayName(defaultConnection) : tx(p.displayName)} {alreadyAdded ? tx('(Eklendi)') : ''}
                    </option>
                  )
                })}
              </optgroup>
              <optgroup label={tx("Özel depolama bağlantıları")}>
                {connections.filter(c => !c.isDefault).map(c => {
                  const descriptor = providers.find(p => p.providerType === c.providerType)
                  const unavail = connectionUnavailableReason(c, descriptor)
                  const alreadyAdded = destinations.some(d => d.storageProvider === c.providerType && d.storageConnectionId === c.id)
                  return (
                    <option key={c.id} value={c.id} disabled={Boolean(unavail) || alreadyAdded}>{connectionDisplayName(c)} ({tx(c.providerDisplayName)}) {unavail ? `· ${unavail}` : ''} {alreadyAdded ? tx('(Eklendi)') : ''}
                    </option>
                  )
                })}
              </optgroup>
            </Select>
          </div>

          <div>
            <label htmlFor="required-replica-count" className="block text-xs font-medium text-text-muted mb-1">{tx("Gerekli doğrulanmış kopya sayısı")}</label>
            <TextInput
              id="required-replica-count"
              type="number"
              min={configuration.enforceThreeTwoOne ? 3 : 1}
              max={destinations.length}
              value={requiredCount}
              aria-invalid={Boolean(replicaError)}
              aria-describedby={replicaError ? 'required-replica-count-error' : undefined}
             
              onChange={e => {
                const count = Number(e.target.value)
                if (!Number.isNaN(count)) updateDestinations(destinations, count)
              }}
            />
            {replicaError && <p id="required-replica-count-error" role="alert" className="text-sm text-error">{replicaError}</p>}
            <p className="text-xs text-text-muted mt-1">
              {destinations.length}{' '}{tx("hedeften en az")}{' '}{requiredCount}{' '}{tx("tanesi doğrulanmalıdır.")}</p>
          </div>
        </div>
        {destinations.length > 1 && (
          <p className="rounded-lg border border-warning-border bg-warning-subtle px-3 py-2 text-xs text-text-heading">
            {tx('Her hedefe ayrı yazılır ve bağımsız olarak doğrulanır. Yedek, gerekli sayıda hedef doğrulandığında başarılı sayılır; başarısız bir hedef sonradan onarılabilir.')}
          </p>
        )}

        <CheckField id="enforce-three-two-one" label={tx('3-2-1 koruması: 3 doğrulanmış kopya, 2 medya sınıfı, 1 doğrulanmış off-site')} checked={Boolean(configuration.enforceThreeTwoOne)}
            disabled={destinations.length < 3}
            onChange={event => onChange({ ...configuration, enforceThreeTwoOne: event.target.checked,
              requiredVerifiedReplicaCount: event.target.checked ? Math.max(3, requiredCount) : requiredCount })} />
        <p className="text-xs text-text-muted">{tx('En az üç hedef ekleyin. Bağlantılarda fiziksel site ve medya sınıfını tanımlayın; uzak bağlantının erişim testini çalıştırın. Aynı site veya aynı medya üzerindeki kopyalar tek başına 3-2-1 sağlamaz.')}</p>
        {configuration.enforceThreeTwoOne && <div className="space-y-1">
          <label htmlFor="backup-source-site" className="block text-sm font-medium">{tx('Kaynak fiziksel site')}</label>
          <TextInput id="backup-source-site" required maxLength={64} pattern="[A-Za-z0-9_-]+"
            aria-invalid={Boolean(replicationError)} aria-describedby={replicationError ? 'replication-error' : undefined}
            value={configuration.sourceSite ?? ''} onChange={event => onChange({ ...configuration, sourceSite: event.target.value })} />
        </div>}
        </div>
      </Disclosure>
    </>
  )
}
