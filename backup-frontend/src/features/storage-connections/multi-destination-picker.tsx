import { tx, useLanguage } from '@/lib/i18n'
import type { BackupConfiguration, BackupDestination } from '@/features/backup-jobs/types'
import { ownershipLabel } from '@/features/storage/labels'
import { errorMessage } from '@/lib/api/failure-messages'
import type { ConnectionView, ProviderDescriptor } from './types'
import { connectionUnavailableReason, primaryDestination, resolveDestinationConnection } from './job-connection-picker-utils'

const selectClass = 'h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring disabled:opacity-60'

interface Props {
  configuration: BackupConfiguration
  connections: readonly ConnectionView[]
  providers: readonly ProviderDescriptor[]
  pending: boolean
  error: unknown
  fieldError?: string
  existingTarget: boolean
  onChange: (configuration: BackupConfiguration) => void
  onRefresh: () => void
}

export function MultiDestinationPicker({ configuration, connections, providers, pending, error, fieldError, existingTarget, onChange, onRefresh }: Props) {
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
      <div className="space-y-1">
        <label htmlFor="primary-storage-provider" className="block text-sm font-medium text-text-heading">{tx("Storage Provider")}</label>
        <select id="primary-storage-provider" className={selectClass} value={primary.storageProvider} disabled={pending || Boolean(error)} onChange={event => {
          const descriptor = providers.find(item => item.providerType === event.target.value)
          if (descriptor?.availability === 'Available' && descriptor.executionLocation && descriptor.topology) {
            replacePrimaryTarget({ storageProvider: descriptor.providerType, storageConnectionId: null, storageExecutionLocation: descriptor.executionLocation })
          }
        }}>
          {!provider && <option value={primary.storageProvider} disabled>{primary.storageProvider}{' '}{tx("· Sağlayıcı bulunamadı")}</option>}
          {providers.map(item => <option key={item.providerType} value={item.providerType} disabled={item.availability !== 'Available' || !item.executionLocation || !item.topology}>{item.displayName}{item.availability !== 'Available' ? tx(' · Yakında / kullanılamıyor') : ''}</option>)}
        </select>
      </div>
      <div className="space-y-1">
        <label htmlFor="primary-storage-connection" className="block text-sm font-medium text-text-heading">{tx("Depolama bağlantısı")}</label>
        <select id="primary-storage-connection" className={selectClass} value={primary.storageConnectionId ?? ''} disabled={pending || Boolean(error)} aria-invalid={Boolean(fieldError)} aria-describedby="storage-connection-help primary-storage-connection-error" onChange={event => chooseConnection(event.target.value)}>
          <option value="" disabled={Boolean(defaultReason)}>{tx("Varsayılan ·")}{' '}{tx(provider?.displayName ?? primary.storageProvider)} · {defaultTarget?.name ?? tx('Bağlantı yok')}{defaultTarget ? ` · ${ownershipLabel(defaultTarget.ownership)}` : ''}{defaultReason ? ` · ${defaultReason}` : tx(' · Kullanılabilir')}</option>
          {selectedMissing && <option value={primary.storageConnectionId!} disabled>{tx("Mevcut bağlantı ·")}{' '}{primary.storageConnectionId}{' '}{tx("· Bağlantı bulunamadı")}</option>}
          {connections.map(connection => {
            const descriptor = providers.find(item => item.providerType === connection.providerType)
            const unavailable = connectionUnavailableReason(connection, descriptor)
            return <option key={connection.id} value={connection.id} disabled={Boolean(unavailable)}>{connection.name} · {connection.providerDisplayName} · {ownershipLabel(connection.ownership)} · {unavailable ?? tx('Kullanılabilir')}</option>
          })}
        </select>
        {fieldError && <p id="primary-storage-connection-error" role="alert" className="text-sm text-destructive">{fieldError}</p>}
        <p id="storage-connection-help" className="text-xs text-text-muted">{tx("Varsayılan seçimi bu sağlayıcının tenant varsayılanına gider. Açık seçim bağlantı kimliğini korur. Son uygunluk kontrolünü sunucu yapar.")}</p>
      </div>
      <div className="space-y-1"><span className="block text-sm font-medium text-text-heading">{tx("Depolama yürütme konumu")}</span><p className="text-sm">{primary.storageExecutionLocation}</p><p className="text-xs text-text-muted">{tx("Yeni hedef seçildiğinde sağlayıcının yürütme konumu uygulanır.")}</p></div>
      {pending && <p role="status" className="text-sm">{tx("Depolama bağlantıları yükleniyor…")}</p>}
      {error ? <div role="alert" className="text-sm text-destructive">{errorMessage(error)} <button type="button" className="underline" onClick={onRefresh}>{tx("Bağlantıları yeniden yükle")}</button></div> : !pending && reason && <p role="status" className="text-sm text-warning md:col-span-2">{target ? tx("“{0}” depolama hedefi", { "0": target.name }) : tx('Depolama hedefi')}: {reason}. {existingTarget ? tx("Mevcut hedef kimliği korunarak iş kaydedilebilir, ancak hedef kullanılabilir olana kadar yeni backup'lar (manuel ve zamanlanmış) başlatılamaz. Mevcut backup'lar etkilenmez.") : tx('Yeni iş veya yeni hedef için kullanılabilir bir bağlantı seçin; varsayılan seçimi bu engeli aşmaz.')}</p>}

      {/* Multi-destination replication section */}
      <div className="space-y-3 pt-4 border-t border-border md:col-span-2">
        <div className="flex items-center justify-between">
          <div>
            <h3 className="text-sm font-medium text-text-heading">{tx("Replikasyon Hedefleri (Multi-Storage Replication)")}</h3>
            <p className="text-xs text-text-muted">{tx("Bu yedekleme işinin birden fazla depolama konumuna kopyalanmasını yapılandırın.")}</p>
          </div>
          <span className="text-xs px-2 py-0.5 rounded bg-surface border border-border">{tx("Toplam")}{' '}{destinations.length}{' '}{tx("hedef")}</span>
        </div>

        <div className="space-y-2">
          {destinations.map((dest, index) => {
            const destConn = dest.storageConnectionId
              ? connections.find(c => c.id === dest.storageConnectionId)
              : connections.find(c => c.providerType === dest.storageProvider && c.isDefault)
            const destProv = providers.find(p => p.providerType === dest.storageProvider)
            const unavailable = connectionUnavailableReason(destConn, destProv)

            return (
              <div key={`${dest.storageProvider}:${dest.storageConnectionId ?? 'default'}`} className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 p-2.5 rounded-md border border-border bg-surface-2 text-sm">
                <div className="space-y-0.5">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-text-heading">{destProv?.displayName ?? dest.storageProvider}</span>
                    {dest.isPrimary ? (
                      <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[11px] font-semibold bg-primary/10 text-primary border border-primary/20">{tx("Primary")}</span>
                    ) : (
                      <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[11px] text-text-muted border border-border">{tx("Replica")}</span>
                    )}
                    {unavailable && (
                      <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[11px] font-semibold bg-destructive/10 text-destructive border border-destructive/20">
                        {unavailable}
                      </span>
                    )}
                  </div>
                  <div className="text-xs text-text-muted">
                    {destConn ? `${destConn.name} (${ownershipLabel(destConn.ownership)})` : (dest.storageConnectionId ? dest.storageConnectionId : tx('Tenant Varsayılanı'))} · {dest.storageExecutionLocation}
                  </div>
                </div>

                <div className="flex items-center gap-2 self-end sm:self-center">
                  {!dest.isPrimary && (
                    <button
                      type="button"
                      onClick={() => handleSetPrimary(index)}
                      className="text-xs text-primary hover:underline px-2 py-1 rounded hover:bg-surface"
                    >{tx("Primary Yap")}</button>
                  )}
                  {destinations.length > 1 && (
                    <button
                      type="button"
                      aria-label={tx("{0} hedefini kaldır", { "0": destProv?.displayName ?? dest.storageProvider })}
                      disabled={Boolean(configuration.enforceThreeTwoOne) && destinations.length <= 3}
                      onClick={() => handleRemove(index)}
                      className="text-xs text-destructive hover:underline px-2 py-1 rounded hover:bg-surface"
                    >{tx("Kaldır")}</button>
                  )}
                </div>
              </div>
            )
          })}
        </div>

        <div className="grid gap-3 sm:grid-cols-2 pt-2">
          <div>
            <label htmlFor="add-replication-destination" className="block text-xs font-medium text-text-muted mb-1">{tx("Yeni Replikasyon Hedefi Ekle")}</label>
            <select
              id="add-replication-destination"
              className={selectClass}
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
              <optgroup label={tx("Varsayılan Sağlayıcı Hedefleri")}>
                {providers.filter(p => p.availability === 'Available' && p.executionLocation).map(p => {
                  const alreadyAdded = destinations.some(d => d.storageProvider === p.providerType && !d.storageConnectionId)
                  return (
                    <option key={`default:${p.providerType}`} value={`default:${p.providerType}`} disabled={alreadyAdded}>{tx("[Replication]")}{' '}{p.displayName}{' '}{tx("· Varsayılan")}{' '}{alreadyAdded ? '(Eklendi)' : ''}
                    </option>
                  )
                })}
              </optgroup>
              <optgroup label={tx("Özel Depolama Bağlantıları")}>
                {connections.map(c => {
                  const descriptor = providers.find(p => p.providerType === c.providerType)
                  const unavail = connectionUnavailableReason(c, descriptor)
                  const alreadyAdded = destinations.some(d => d.storageProvider === c.providerType && d.storageConnectionId === c.id)
                  return (
                    <option key={c.id} value={c.id} disabled={Boolean(unavail) || alreadyAdded}>{tx("Replication-Hedef:")}{' '}{c.name} ({c.providerDisplayName}) {unavail ? `· ${unavail}` : ''} {alreadyAdded ? '(Eklendi)' : ''}
                    </option>
                  )
                })}
              </optgroup>
            </select>
          </div>

          <div>
            <label htmlFor="required-replica-count" className="block text-xs font-medium text-text-muted mb-1">{tx("Gerekli Doğrulanmış Kopya Sayısı (Required Verified Replicas)")}</label>
            <input
              id="required-replica-count"
              type="number"
              min={configuration.enforceThreeTwoOne ? 3 : 1}
              max={destinations.length}
              value={requiredCount}
              className={selectClass}
              onChange={e => {
                const count = Number(e.target.value)
                if (!Number.isNaN(count)) updateDestinations(destinations, count)
              }}
            />
            <p className="text-[11px] text-text-muted mt-1">
              {destinations.length}{' '}{tx("hedeften en az")}{' '}{requiredCount}{' '}{tx("tanesi doğrulanmalıdır.")}</p>
          </div>
        </div>
        {destinations.length > 1 && (
          <p className="rounded-md border border-warning/40 bg-warning-subtle/30 px-3 py-2 text-xs text-text-heading">
            {tx('Her hedefe ayrı yazılır ve bağımsız olarak doğrulanır. Yedek, gerekli sayıda hedef doğrulandığında başarılı sayılır; başarısız bir hedef sonradan onarılabilir.')}
          </p>
        )}

        <label htmlFor="enforce-three-two-one" className="flex items-center gap-2 text-sm">
          <input id="enforce-three-two-one" type="checkbox" checked={Boolean(configuration.enforceThreeTwoOne)}
            disabled={destinations.length < 3}
            onChange={event => onChange({ ...configuration, enforceThreeTwoOne: event.target.checked,
              requiredVerifiedReplicaCount: event.target.checked ? Math.max(3, requiredCount) : requiredCount })} />
          {tx('3-2-1 koruması: 3 doğrulanmış kopya, 2 medya sınıfı, 1 doğrulanmış off-site')}
        </label>
        <p className="text-xs text-text-muted">{tx('En az üç hedef ekleyin. Bağlantılarda fiziksel site ve medya sınıfını tanımlayın; uzak bağlantının erişim testini çalıştırın. Aynı site veya aynı medya üzerindeki kopyalar tek başına 3-2-1 sağlamaz.')}</p>
        {configuration.enforceThreeTwoOne && <div className="space-y-1">
          <label htmlFor="backup-source-site" className="block text-sm font-medium">{tx('Kaynak fiziksel site')}</label>
          <input id="backup-source-site" className={selectClass} required maxLength={64} pattern="[A-Za-z0-9_-]+"
            value={configuration.sourceSite ?? ''} onChange={event => onChange({ ...configuration, sourceSite: event.target.value })} />
        </div>}
      </div>
    </>
  )
}
