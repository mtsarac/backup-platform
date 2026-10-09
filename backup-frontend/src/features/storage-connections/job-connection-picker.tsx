import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { ownershipLabel } from '@/features/storage/labels'
import { errorMessage } from '@/lib/api/failure-messages'
import type { ConnectionView, ProviderDescriptor } from './types'
import { connectionUnavailableReason, resolveJobConnection } from './job-connection-picker-utils'

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

/** Catalog data determines new selections; an existing explicit id is never rewritten by a refetch. */
export function JobConnectionPicker({ configuration, connections, providers, pending, error, fieldError, existingTarget, onChange, onRefresh }: Props) {
  const target = resolveJobConnection(configuration, connections)
  const provider = providers.find(item => item.providerType === configuration.storageProvider)
  const targetProvider = providers.find(item => item.providerType === target?.providerType)
  const reason = connectionUnavailableReason(target, targetProvider)
  const defaultTarget = connections.find(item => item.providerType === configuration.storageProvider && item.isDefault)
  const defaultReason = connectionUnavailableReason(defaultTarget, provider)
  const selectedMissing = configuration.storageConnectionId && !target

  function chooseConnection(id: string) {
    if (!id) {
      if (!defaultReason && provider?.executionLocation) onChange({ ...configuration, storageConnectionId: null, storageExecutionLocation: provider.executionLocation })
      return
    }
    const connection = connections.find(item => item.id === id)
    const descriptor = providers.find(item => item.providerType === connection?.providerType)
    if (connection && descriptor?.executionLocation && !connectionUnavailableReason(connection, descriptor)) {
      onChange({ ...configuration, storageConnectionId: connection.id, storageProvider: connection.providerType, storageExecutionLocation: descriptor.executionLocation })
    }
  }

  return <>
    <div className="space-y-1">
      <label htmlFor="configuration.storageProvider" className="block text-sm font-medium text-text-heading">Storage Provider</label>
      <select id="configuration.storageProvider" className={selectClass} value={configuration.storageProvider} disabled={pending || Boolean(error)} onChange={event => {
        const descriptor = providers.find(item => item.providerType === event.target.value)
        if (descriptor?.availability === 'Available' && descriptor.executionLocation && descriptor.topology) onChange({ ...configuration, storageProvider: descriptor.providerType, storageConnectionId: null, storageExecutionLocation: descriptor.executionLocation })
      }}>
        {!provider && <option value={configuration.storageProvider} disabled>{configuration.storageProvider} · Sağlayıcı bulunamadı</option>}
        {providers.map(item => <option key={item.providerType} value={item.providerType} disabled={item.availability !== 'Available' || !item.executionLocation || !item.topology}>{item.displayName}{item.availability !== 'Available' ? ' · Yakında / kullanılamıyor' : ''}</option>)}
      </select>
    </div>
    <div className="space-y-1">
      <label htmlFor="configuration.storageConnectionId" className="block text-sm font-medium text-text-heading">Depolama bağlantısı</label>
      <select id="configuration.storageConnectionId" className={selectClass} value={configuration.storageConnectionId ?? ''} disabled={pending || Boolean(error)} aria-invalid={Boolean(fieldError)} aria-describedby="storage-connection-help configuration.storageConnectionId-error" onChange={event => chooseConnection(event.target.value)}>
        <option value="" disabled={Boolean(defaultReason)}>Varsayılan · {provider?.displayName ?? configuration.storageProvider} · {defaultTarget?.name ?? 'Bağlantı yok'}{defaultTarget ? ` · ${ownershipLabel(defaultTarget.ownership)}` : ''}{defaultReason ? ` · ${defaultReason}` : ' · Kullanılabilir'}</option>
        {selectedMissing && <option value={configuration.storageConnectionId!} disabled>Mevcut bağlantı · {configuration.storageConnectionId} · Bağlantı bulunamadı</option>}
        {connections.map(connection => {
          const descriptor = providers.find(item => item.providerType === connection.providerType)
          const unavailable = connectionUnavailableReason(connection, descriptor)
          return <option key={connection.id} value={connection.id} disabled={Boolean(unavailable)}>{connection.name} · {connection.providerDisplayName} · {ownershipLabel(connection.ownership)} · {unavailable ?? 'Kullanılabilir'}</option>
        })}
      </select>
      {fieldError && <p id="configuration.storageConnectionId-error" role="alert" className="text-sm text-destructive">{fieldError}</p>}
      <p id="storage-connection-help" className="text-xs text-text-muted">Varsayılan seçimi bu sağlayıcının tenant varsayılanına gider. Açık seçim bağlantı kimliğini korur. Son uygunluk kontrolünü sunucu yapar.</p>
    </div>
    <div className="space-y-1"><span className="block text-sm font-medium text-text-heading">Depolama yürütme konumu</span><p className="text-sm">{configuration.storageExecutionLocation}</p><p className="text-xs text-text-muted">Yeni hedef seçildiğinde sağlayıcının yürütme konumu uygulanır.</p></div>
    {pending && <p role="status" className="text-sm">Depolama bağlantıları yükleniyor…</p>}
    {error ? <div role="alert" className="text-sm text-destructive">{errorMessage(error)} <button type="button" className="underline" onClick={onRefresh}>Bağlantıları yeniden yükle</button></div> : !pending && reason && <p role="status" className="text-sm text-warning md:col-span-2">{target ? `“${target.name}” depolama hedefi` : 'Depolama hedefi'}: {reason}. {existingTarget ? "Mevcut hedef kimliği korunarak iş kaydedilebilir, ancak hedef kullanılabilir olana kadar yeni backup'lar (manuel ve zamanlanmış) başlatılamaz. Mevcut backup'lar etkilenmez." : 'Yeni iş veya yeni hedef için kullanılabilir bir bağlantı seçin; varsayılan seçimi bu engeli aşmaz.'}</p>}
  </>
}
