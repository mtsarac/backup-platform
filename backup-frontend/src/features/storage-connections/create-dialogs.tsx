import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useState } from 'react'
import { FormDialog } from '@/components/ui/form-dialog'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import { QUOTA_SCOPE_PATTERN } from '@/features/storage/scope-input'
import { activeSecretFields, configurationComplete, initialFieldValues, providerPayload, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import { useCreatePlatformConnection, usePlatformProviders } from './hooks'
import { errorField, isPlatformCreatable } from './labels'
import { ProviderFields } from './provider-form'

interface PickedTenant { tenantId: string; name: string }


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
