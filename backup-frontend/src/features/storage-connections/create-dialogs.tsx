import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { usePlatformTenants } from '@/features/platform/hooks'
import { inputClass, selectClass } from '@/features/platform/styles'
import { QUOTA_SCOPE_PATTERN } from '@/features/storage/scope-input'
import { ConfigFieldInputs, SecretFieldInputs } from './descriptor-fields'
import { activeSecretFields, buildConfiguration, buildSecretValues, configurationComplete, hasSecretInput, initialFieldValues, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import { storageConnectionKeys, useCreatePlatformConnection, useCreateTenantConnection, usePlatformProviders } from './hooks'
import { getGoogleDriveAuthorizeUrl, testTenantConnection } from './api'
import { openGoogleOAuthPopup } from './google-oauth'
import { connectionErrorMessage, errorField, invalidFieldName, isPlatformCreatable } from './labels'
import type { ProviderDescriptor, TenantCreateConnectionInput } from './types'

/** Safe descriptor configuration followed by the selected authentication method's new credentials. */
function ProviderFields({ provider, config, secret, error, onConfig, onSecret }: {
  provider: ProviderDescriptor; config: FieldValues; secret: Record<string, string>; error: unknown
  onConfig: (name: string, value: string | boolean) => void; onSecret: (name: string, value: string) => void
}) {
  const fields = activeSecretFields(provider.providerType, config, provider.secretFields)
  return <>
    <ConfigFieldInputs providerType={provider.providerType} fields={provider.configFields} values={config} invalidName={invalidFieldName(error, 'configuration')} onChange={onConfig} />
    {fields.length > 0 && <div className="space-y-3 rounded-lg border border-border bg-surface-2 p-3">
      <p className="text-xs text-text-muted">Kimlik bilgisi değerleri yalnızca sunucuya gönderilir; kaydedildikten sonra bir daha gösterilmez.</p>
      <SecretFieldInputs providerType={provider.providerType} fields={fields} values={secret} invalidName={invalidFieldName(error, 'secret')} onChange={onSecret} />
    </div>}
  </>
}

/** `configuration`/`secret` are omitted entirely when the provider declares no such fields or nothing was entered. */
function providerPayload(provider: ProviderDescriptor, name: string, config: FieldValues, secret: Record<string, string>): TenantCreateConnectionInput {
  const input: TenantCreateConnectionInput = { providerType: provider.providerType, name: name.trim() }
  if (provider.configFields.length > 0) input.configuration = buildConfiguration(provider.configFields, config)
  const fields = activeSecretFields(provider.providerType, config, provider.secretFields)
  const credentials = buildSecretValues(fields, secret)
  if (hasSecretInput(credentials)) input.secret = credentials
  return input
}

/** Tenant 'Bağlantı ekle': generic over the descriptor, so a provider that becomes tenant-creatable needs no UI change. */
export function CreateConnectionDialog({ provider, onClose }: { provider: ProviderDescriptor; onClose: () => void }) {
  const [name, setName] = useState('')
  const [config, setConfig] = useState<FieldValues>(() => initialFieldValues(provider.configFields, {}, provider.providerType))
  const [secret, setSecret] = useState<Record<string, string>>({})
  const create = useCreateTenantConnection()
  const client = useQueryClient()
  const reset = create.reset
  useEffect(() => reset, [reset])

  function submit() {
    const input = providerPayload(provider, name, config, secret)
    setSecret({})
    create.mutate(input, {
      onSuccess: (newConnection) => {
        reset()
        onClose()
        if (provider.providerType === 'google-drive') {
          getGoogleDriveAuthorizeUrl(newConnection.tenantId, newConnection.id).then(({ authorizationUrl }) => {
            openGoogleOAuthPopup(authorizationUrl, () => {
              // The test records its result on the connection either way; refresh the list so the new state is shown.
              testTenantConnection(newConnection.tenantId, newConnection.id).catch(() => {})
                .finally(() => client.invalidateQueries({ queryKey: storageConnectionKeys.tenantAll(newConnection.tenantId) }))
            })
          }).catch(() => {})
        }
      }
    })
  }

  return <FormDialog title={`Bağlantı ekle: ${provider.displayName}`} submitLabel="Bağlantı ekle" pendingLabel="Ekleniyor…" pending={create.isPending}
    disabled={!name.trim() || !configurationComplete(provider.configFields, config) || !secretsComplete(activeSecretFields(provider.providerType, config, provider.secretFields), secret)}
    error={create.error ? connectionErrorMessage(create.error) : undefined} onSubmit={submit} onClose={onClose}>
    <label className="block text-sm text-text-muted">Ad
      <input className={inputClass} value={name} required aria-invalid={errorField(create.error) === 'name'} onChange={(event) => setName(event.target.value)} />
    </label>
    <ProviderFields provider={provider} config={config} secret={secret} error={create.error}
      onConfig={(field, value) => setConfig((current) => ({ ...current, [field]: value }))} onSecret={(field, value) => setSecret((current) => ({ ...current, [field]: value }))} />
  </FormDialog>
}

interface PickedTenant { tenantId: string; name: string }

/** Search + select, like the member picker: a plain button, not a nested form, because this lives inside the dialog's form. */
function TenantPicker({ value, onChange }: { value: string; onChange: (tenantId: string) => void }) {
  const [draft, setDraft] = useState('')
  const [search, setSearch] = useState('')
  const tenants = usePlatformTenants({ search, page: 1, pageSize: 20 })
  return <div className="space-y-2">
    <div className="flex items-end gap-2">
      <label className="min-w-0 flex-1 text-sm text-text-muted">Tenant ara
        <input className={inputClass} value={draft} placeholder="Tenant adı" onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); setSearch(draft.trim()); onChange('') } }} />
      </label>
      <Button type="button" variant="outline" className="h-9" onClick={() => { setSearch(draft.trim()); onChange('') }}>Ara</Button>
    </div>
    <label className="block text-sm text-text-muted">Tenant
      <select className={`${selectClass} w-full`} value={value} onChange={(event) => onChange(event.target.value)} disabled={tenants.isPending}>
        <option value="">{tenants.isPending ? 'Tenant\'lar yükleniyor…' : tenants.error ? 'Tenant\'lar alınamadı' : 'Tenant seçin'}</option>
        {tenants.data?.items.map((tenant) => <option key={tenant.tenantId} value={tenant.tenantId}>{tenant.name}</option>)}
      </select>
    </label>
    {tenants.data && tenants.data.totalCount > tenants.data.items.length && <p className="text-xs text-text-muted">İlk {tenants.data.items.length} tenant gösteriliyor; daraltmak için arayın.</p>}
  </div>
}

/** PlatformAdmin 'Managed target ekle': a PlatformManaged connection for a chosen tenant (fixed when opened from the tenant's own page). */
export function CreateManagedTargetDialog({ tenant, onClose }: { tenant?: PickedTenant; onClose: () => void }) {
  const providers = usePlatformProviders()
  const creatable = (providers.data?.items ?? []).filter(isPlatformCreatable)
  const [tenantId, setTenantId] = useState(tenant?.tenantId ?? '')
  const [providerType, setProviderType] = useState('')
  const [name, setName] = useState('')
  const [quotaScope, setQuotaScope] = useState('')
  const [config, setConfig] = useState<FieldValues>({})
  const [secret, setSecret] = useState<Record<string, string>>({})
  const create = useCreatePlatformConnection()
  const reset = create.reset
  useEffect(() => reset, [reset])
  const provider = creatable.find((item) => item.providerType === providerType)
  const quotaScopeInvalid = quotaScope.trim() !== '' && !QUOTA_SCOPE_PATTERN.test(quotaScope.trim())

  function chooseProvider(next: string) {
    setProviderType(next)
    const selected = creatable.find((item) => item.providerType === next)
    setConfig(initialFieldValues(selected?.configFields ?? [], {}, next))
    setSecret({})
  }

  function submit() {
    if (!provider) return
    const input = { ...providerPayload(provider, name, config, secret), ownership: 'PlatformManaged' as const, ...(quotaScope.trim() ? { quotaScope: quotaScope.trim() } : {}) }
    setSecret({})
    create.mutate({ tenantId, input }, { onSuccess: () => { reset(); onClose() } })
  }

  return <FormDialog title={tenant ? `Managed target ekle: ${tenant.name}` : 'Managed target ekle'}
    description="Seçilen tenant için platform tarafından işletilen (PlatformManaged) bir depolama hedefi oluşturur."
    submitLabel="Managed target ekle" pendingLabel="Ekleniyor…" pending={create.isPending}
    disabled={!tenantId || !provider || !name.trim() || quotaScopeInvalid || !configurationComplete(provider.configFields, config) || !secretsComplete(activeSecretFields(provider.providerType, config, provider.secretFields), secret)}
    error={create.error ? connectionErrorMessage(create.error) : providers.error ? connectionErrorMessage(providers.error) : undefined} onSubmit={submit} onClose={onClose}>
    {tenant ? <p className="text-sm text-text-muted">Tenant: <span className="font-medium text-text">{tenant.name}</span></p> : <TenantPicker value={tenantId} onChange={setTenantId} />}
    <label className="block text-sm text-text-muted">Storage Provider
      <select className={`${selectClass} w-full`} value={providerType} onChange={(event) => chooseProvider(event.target.value)} disabled={providers.isPending}>
        <option value="">{providers.isPending ? 'Sağlayıcılar yükleniyor…' : creatable.length === 0 ? 'Oluşturulabilir sağlayıcı yok' : 'Sağlayıcı seçin'}</option>
        {creatable.map((item) => <option key={item.providerType} value={item.providerType}>{item.displayName}</option>)}
      </select>
    </label>
    <label className="block text-sm text-text-muted">Ad
      <input className={inputClass} value={name} required aria-invalid={errorField(create.error) === 'name'} onChange={(event) => setName(event.target.value)} />
    </label>
    <label className="block text-sm text-text-muted">Kota kapsamı (isteğe bağlı)
      <input className={inputClass} value={quotaScope} maxLength={64} placeholder="managed" aria-invalid={quotaScopeInvalid || errorField(create.error) === 'quotaScope'} onChange={(event) => setQuotaScope(event.target.value)} />
      <span className="mt-1 block text-xs">Boş bırakılırsa sunucu varsayılanı uygulanır. Küçük harf, rakam ve “-”, 1-64 karakter.</span>
      {quotaScopeInvalid && <span role="alert" className="mt-1 block text-xs text-error">Kota kapsamı yalnızca küçük harf, rakam ve “-” içerebilir (1-64 karakter).</span>}
    </label>
    {provider && <ProviderFields provider={provider} config={config} secret={secret} error={create.error}
      onConfig={(field, value) => setConfig((current) => ({ ...current, [field]: value }))} onSecret={(field, value) => setSecret((current) => ({ ...current, [field]: value }))} />}
  </FormDialog>
}
