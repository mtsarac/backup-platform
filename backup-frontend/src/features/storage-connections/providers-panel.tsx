import { Button } from '@/components/ui/button'
import { Fact } from '@/features/storage/quota-summary'
import { ownershipLabel } from '@/features/storage/labels'
import { AvailabilityBadge, CapabilityChips } from './connection-parts'
import { executionLocationLabel, tenantCreateState, testModeLabel, topologyLabel } from './labels'
import type { ConnectionView, ProviderDescriptor } from './types'

function ProviderCard({ provider, connections, canAdd, onAdd }: { provider: ProviderDescriptor; connections: readonly ConnectionView[]; canAdd: boolean; onAdd: (provider: ProviderDescriptor) => void }) {
  const planned = provider.availability !== 'Available'
  const create = tenantCreateState(provider, connections)
  return <li aria-disabled={planned || undefined} className={`space-y-3 rounded-lg border border-border bg-surface-2 p-3 ${planned ? 'opacity-70' : ''}`}>
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="font-medium text-text-heading">{provider.displayName}</h3>
      <AvailabilityBadge availability={provider.availability} />
    </div>
    {planned
      ? <p className="text-sm text-text-muted">Yakında / henüz kullanılamaz. Bu sağlayıcı için bağlantı oluşturulamaz ve test başarılı sayılmaz.</p>
      : <dl className="grid gap-3 sm:grid-cols-2">
        <Fact label="Topoloji">{topologyLabel(provider.topology)}</Fact>
        <Fact label="Yürütme konumu">{executionLocationLabel(provider.executionLocation)}</Fact>
        <Fact label="Test">{testModeLabel(provider.testMode)}</Fact>
        <Fact label="Sahiplik">{provider.supportedOwnerships.map(ownershipLabel).join(', ') || '—'}</Fact>
      </dl>}
    <CapabilityChips capabilities={provider.capabilities} label={`${provider.displayName} yetenekleri`} />
    {!planned && (create.creatable
      ? canAdd
        ? <Button variant="outline" size="sm" aria-label={`${provider.displayName} için bağlantı ekle`} onClick={() => onAdd(provider)}>Bağlantı ekle</Button>
        : <p className="text-xs text-text-muted">Bağlantı eklemek için Owner / Admin rolü gerekir.</p>
      : <p className="text-xs text-text-muted">{create.reason}.</p>)}
  </li>
}

/** What each Storage Provider can do. Planned providers are listed for capability display only: no button, no dialog. */
export function ProvidersPanel({ providers, connections, canAdd, onAdd }: { providers: readonly ProviderDescriptor[]; connections: readonly ConnectionView[]; canAdd: boolean; onAdd: (provider: ProviderDescriptor) => void }) {
  return <ul className="grid gap-3 lg:grid-cols-2">
    {providers.map((provider) => <ProviderCard key={provider.providerType} provider={provider} connections={connections} canAdd={canAdd} onAdd={onAdd} />)}
  </ul>
}
