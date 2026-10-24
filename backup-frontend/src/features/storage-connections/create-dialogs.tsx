import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { FormDialog } from '@/components/ui/form-dialog'
import { usePlatformTenants } from '@/features/platform/hooks'
import { inputClass, selectClass } from '@/features/platform/styles'
import { QUOTA_SCOPE_PATTERN } from '@/features/storage/scope-input'
import { activeSecretFields, configurationComplete, initialFieldValues, providerPayload, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import { useCreatePlatformConnection, usePlatformProviders } from './hooks'
import { errorField, isPlatformCreatable } from './labels'
import { ProviderFields } from './provider-form'

interface PickedTenant { tenantId: string; name: string }

/** Search + select, like the member picker: a plain button, not a nested form, because this lives inside the dialog's form. */
function TenantPicker({ value, onChange }: { value: string; onChange: (tenantId: string) => void }) {
  useLanguage()
  const [draft, setDraft] = useState('')
  const [search, setSearch] = useState('')
  const tenants = usePlatformTenants({ search, page: 1, pageSize: 20 })
  return <div className="space-y-2">
    <div className="flex items-end gap-2">
      <label className="min-w-0 flex-1 text-sm text-text-muted">{tx("Tenant ara")}<input className={inputClass} value={draft} placeholder={tx("Tenant adı")} onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); setSearch(draft.trim()); onChange('') } }} />
      </label>
      <Button type="button" variant="outline" className="h-9" onClick={() => { setSearch(draft.trim()); onChange('') }}>{tx("Ara")}</Button>
    </div>
    <label className="block text-sm text-text-muted">{tx("Tenant")}<select className={`${selectClass} w-full`} value={value} onChange={(event) => onChange(event.target.value)} disabled={tenants.isPending}>
        <option value="">{tenants.isPending ? tx('Tenant\'lar yükleniyor…') : tenants.error ? tx('Tenant\'lar alınamadı') : tx('Tenant seçin')}</option>
        {tenants.data?.items.map((tenant) => <option key={tenant.tenantId} value={tenant.tenantId}>{tenant.name}</option>)}
      </select>
    </label>
    {tenants.data && tenants.data.totalCount > tenants.data.items.length && <p className="text-xs text-text-muted">{tx("İlk")}{' '}{tenants.data.items.length}{' '}{tx("tenant gösteriliyor; daraltmak için arayın.")}</p>}
  </div>
}

/** PlatformAdmin 'Managed target ekle': a PlatformManaged connection for a chosen tenant (fixed when opened from the tenant's own page). */
export function CreateManagedTargetDialog({ tenant, onClose }: { tenant?: PickedTenant; onClose: () => void }) {
  useLanguage()
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

  return <FormDialog title={tenant ? tx("Managed target ekle: {0}", { "0": tenant.name }) : tx('Managed target ekle')}
    description={tx("Seçilen tenant için platform tarafından işletilen (PlatformManaged) bir depolama hedefi oluşturur.")}
    submitLabel={tx("Managed target ekle")} pendingLabel={tx("Ekleniyor…")} pending={create.isPending}
    disabled={!tenantId || !provider || !name.trim() || quotaScopeInvalid || !configurationComplete(provider.configFields, config) || !secretsComplete(activeSecretFields(provider.providerType, config, provider.secretFields), secret)}
    error={create.error ?? providers.error} errorScope="storageConnection" onSubmit={submit} onClose={onClose}>
    {tenant ? <p className="text-sm text-text-muted">{tx("Tenant:")}{' '}<span className="font-medium text-text">{tenant.name}</span></p> : <TenantPicker value={tenantId} onChange={setTenantId} />}
    <label className="block text-sm text-text-muted">{tx("Storage Provider")}<select className={`${selectClass} w-full`} value={providerType} onChange={(event) => chooseProvider(event.target.value)} disabled={providers.isPending}>
        <option value="">{providers.isPending ? tx('Sağlayıcılar yükleniyor…') : creatable.length === 0 ? tx('Oluşturulabilir sağlayıcı yok') : tx('Sağlayıcı seçin')}</option>
        {creatable.map((item) => <option key={item.providerType} value={item.providerType}>{item.displayName}</option>)}
      </select>
    </label>
    <label className="block text-sm text-text-muted">{tx("Ad")}<input className={inputClass} value={name} required aria-invalid={errorField(create.error) === 'name'} onChange={(event) => setName(event.target.value)} />
    </label>
    <label className="block text-sm text-text-muted">{tx("Kota kapsamı (isteğe bağlı)")}<input className={inputClass} value={quotaScope} maxLength={64} placeholder="managed" aria-invalid={quotaScopeInvalid || errorField(create.error) === 'quotaScope'} onChange={(event) => setQuotaScope(event.target.value)} />
      <span className="mt-1 block text-xs">{tx("Boş bırakılırsa sunucu varsayılanı uygulanır. Küçük harf, rakam ve “-”, 1-64 karakter.")}</span>
      {quotaScopeInvalid && <span role="alert" className="mt-1 block text-xs text-error">{tx("Kota kapsamı yalnızca küçük harf, rakam ve “-” içerebilir (1-64 karakter).")}</span>}
    </label>
    {provider && <ProviderFields provider={provider} config={config} secret={secret} error={create.error}
      onConfig={(field, value) => setConfig((current) => ({ ...current, [field]: value }))} onSecret={(field, value) => setSecret((current) => ({ ...current, [field]: value }))} />}
  </FormDialog>
}
