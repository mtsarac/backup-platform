import { Link } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button-variants'
import { Facts, Fact } from '@/components/ui/section'
import { ownershipLabel } from '@/features/storage/labels'
import { AvailabilityBadge, CapabilityChips } from './connection-parts'
import { executionLocationLabel, tenantCreateState, testModeLabel, topologyLabel } from './labels'
import { ProviderIcon } from './provider-icon'
import type { ConnectionView, ProviderDescriptor } from './types'

function ProviderCard({ provider, connections, addPath }: { provider: ProviderDescriptor; connections: readonly ConnectionView[]; addPath?: string }) {
  const planned = provider.availability !== 'Available'
  const create = tenantCreateState(provider, connections)
  return <li aria-disabled={planned || undefined} className={`space-y-4 rounded-xl border border-border bg-surface p-5 shadow-card ${planned ? 'opacity-70' : ''}`}>
    <div className="flex items-start gap-3">
      <ProviderIcon providerType={provider.providerType} />
      <div className="min-w-0 flex-1"><h3 className="font-medium text-text-heading">{provider.displayName}</h3><div className="mt-1"><AvailabilityBadge availability={provider.availability} /></div></div>
    </div>
    {planned
      ? <p className="text-sm text-text-muted">Yakında / henüz kullanılamaz. Bu sağlayıcı için bağlantı oluşturulamaz ve test başarılı sayılmaz.</p>
      : <Facts>
        <Fact label="Topoloji">{topologyLabel(provider.topology)}</Fact>
        <Fact label="Yürütme konumu">{executionLocationLabel(provider.executionLocation)}</Fact>
        <Fact label="Test">{testModeLabel(provider.testMode)}</Fact>
        <Fact label="Sahiplik">{provider.supportedOwnerships.map(ownershipLabel).join(', ') || '—'}</Fact>
      </Facts>}
    <CapabilityChips capabilities={provider.capabilities} label={`${provider.displayName} yetenekleri`} />
    {!planned && (create.creatable
      ? addPath
        ? <Link to={`${addPath}?provider=${encodeURIComponent(provider.providerType)}`} aria-label={`${provider.displayName} için bağlantı ekle`} className={buttonVariants({ variant: 'outline', size: 'sm' })}><Plus aria-hidden="true" className="size-4" />Bağlantı ekle</Link>
        : <p className="text-sm text-text-muted">Bağlantı eklemek için Owner / Admin rolü gerekir.</p>
      : <p className="text-sm text-text-muted">{create.reason}.</p>)}
  </li>
}

/** What each Storage Provider can do. Planned providers are listed for capability display only: no link, no wizard. `addPath` is set only for roles that may add. */
export function ProvidersPanel({ providers, connections, addPath }: { providers: readonly ProviderDescriptor[]; connections: readonly ConnectionView[]; addPath?: string }) {
  return <ul className="grid gap-4 lg:grid-cols-2">
    {providers.map((provider) => <ProviderCard key={provider.providerType} provider={provider} connections={connections} addPath={addPath} />)}
  </ul>
}
