import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button-variants'
import { Facts, Fact } from '@/components/ui/section'
import { ownershipLabel } from '@/features/storage/labels'
import { CapabilityChips } from './connection-parts'
import { executionLocationLabel, tenantCreateState, testModeLabel, topologyLabel } from './labels'
import { ProviderIcon } from './provider-icon'
import type { ConnectionView, ProviderDescriptor } from './types'
import { StatusPill } from '@/components/data/status-pill'

function ProviderCard({ provider, connections, addPath }: { provider: ProviderDescriptor; connections: readonly ConnectionView[]; addPath?: string }) {
  useLanguage()
  const planned = provider.availability !== 'Available'
  const create = tenantCreateState(provider, connections)
  return <li aria-disabled={planned || undefined} className={`space-y-4 rounded-xl border border-border bg-surface p-5 shadow-card ${planned ? 'opacity-70' : ''}`}>
    <div className="flex items-start gap-3">
      <ProviderIcon providerType={provider.providerType} />
      <div className="min-w-0 flex-1"><h3 className="font-medium text-text-heading">{tx(provider.displayName)}</h3><div className="mt-1"><StatusPill domain="provider" status={provider.availability} /></div></div>
    </div>
    {planned
      ? <p className="text-sm text-text-muted">{tx("Yakında / henüz kullanılamaz. Bu sağlayıcı için bağlantı oluşturulamaz ve test başarılı sayılmaz.")}</p>
      : <Facts>
        <Fact label={tx("Topoloji")}>{topologyLabel(provider.topology)}</Fact>
        <Fact label={tx("Yürütme konumu")}>{executionLocationLabel(provider.executionLocation)}</Fact>
        <Fact label="Test">{testModeLabel(provider.testMode)}</Fact>
        <Fact label={tx("Sahiplik")}>{provider.supportedOwnerships.map(ownershipLabel).join(', ') || '—'}</Fact>
      </Facts>}
    <CapabilityChips capabilities={provider.capabilities} label={tx("{0} yetenekleri", { "0": tx(provider.displayName) })} />
    {!planned && (create.creatable
      ? addPath
        ? <Link to={`${addPath}?provider=${encodeURIComponent(provider.providerType)}`} aria-label={tx("{0} için bağlantı ekle", { "0": tx(provider.displayName) })} className={buttonVariants({ variant: 'outline', size: 'sm' })}><Plus aria-hidden="true" className="size-4" />{tx("Bağlantı ekle")}</Link>
        : <p className="text-sm text-text-muted">{tx("Bağlantı eklemek için Owner / Admin rolü gerekir.")}</p>
      : <p className="text-sm text-text-muted">{create.reason}.</p>)}
  </li>
}

/** What each Storage Provider can do. Planned providers are listed for capability display only: no link, no wizard. `addPath` is set only for roles that may add. */
export function ProvidersPanel({ providers, connections, addPath }: { providers: readonly ProviderDescriptor[]; connections: readonly ConnectionView[]; addPath?: string }) {
  useLanguage()
  return <ul className="grid gap-4 lg:grid-cols-2">
    {providers.map((provider) => <ProviderCard key={provider.providerType} provider={provider} connections={connections} addPath={addPath} />)}
  </ul>
}
