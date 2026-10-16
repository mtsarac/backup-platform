import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Field } from '@/components/ui/field'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import { FormDialog } from '@/features/platform/plans/form-dialog'
import { inputClass, selectClass } from '@/features/platform/styles'
import { parseQuotaScopeLabels, parseStorageConnectionIds, type ScopeValuesResult } from '@/features/storage/scope-input'
import { QuotaConnectionPicker } from '@/features/storage-connections/quota-connection-picker'
import type { AccountingMode, EnforcementMode, QuotaPolicy, ScopeKind, ScopeSelector } from '@/features/storage/types'
import { useCreateQuotaPolicy, useUpdateQuotaPolicy } from './hooks'

interface Props {
  policy?: QuotaPolicy
  onClose: () => void
}

export function QuotaPolicyDialog({ policy, onClose }: Props) {
  useLanguage()
  const isEditing = Boolean(policy)

  const [key, setKey] = useState(policy?.key ?? '')
  const [name, setName] = useState(policy?.name ?? '')
  const [accountingMode, setAccountingMode] = useState<AccountingMode>(policy?.accountingMode ?? 'LogicalArtifactBytes')
  const [enforcementMode, setEnforcementMode] = useState<EnforcementMode>(policy?.enforcementMode ?? 'BlockNewBackups')
  const [warningThreshold, setWarningThreshold] = useState(policy?.warningThresholdPercent ?? 80)
  const [criticalThreshold, setCriticalThreshold] = useState(policy?.criticalThresholdPercent ?? 95)
  const [enabled, setEnabled] = useState(policy?.enabled ?? true)

  // Scope editing state
  const initialScope = policy?.includedStorageScopes?.[0]
  const [scopeKind, setScopeKind] = useState<ScopeKind>(initialScope?.kind ?? 'all')
  const [providerTypes, setProviderTypes] = useState<string>((initialScope?.kind === 'providerType' ? initialScope.values ?? [] : ['central']).join(', '))
  const [ownershipTypes, setOwnershipTypes] = useState<string[]>(initialScope?.kind === 'ownershipType' ? initialScope.values ?? ['PlatformManaged'] : ['PlatformManaged'])
  const [quotaScopes, setQuotaScopes] = useState<string>((initialScope?.kind === 'quotaScope' ? initialScope.values ?? [] : ['managed']).join(', '))
  const [connectionIds, setConnectionIds] = useState<string>((initialScope?.kind === 'storageConnection' ? initialScope.values ?? [] : []).join('\n'))
  const [scopeError, setScopeError] = useState<string | null>(null)

  const create = useCreateQuotaPolicy()
  const update = useUpdateQuotaPolicy(policy?.policyId ?? '')

  const mutation = isEditing ? update : create

  function toggleOwnership(val: 'PlatformManaged' | 'TenantManaged') {
    setOwnershipTypes((prev) =>
      prev.includes(val) ? prev.filter((item) => item !== val) : [...prev, val],
    )
  }

  function handleSubmit() {
    setScopeError(null)
    let includedStorageScopes: ScopeSelector[]

    if (scopeKind === 'all') {
      includedStorageScopes = [{ kind: 'all' }]
    } else if (scopeKind === 'providerType') {
      const providers = providerTypes
        .split(',')
        .map((p) => p.trim().toLowerCase())
        .filter((p) => p.length > 0)
      if (providers.length === 0) {
        setScopeError(tx('En az bir provider türü girmelisiniz (örn. central, local).'))
        return
      }
      includedStorageScopes = [{ kind: 'providerType', values: providers }]
    } else if (scopeKind === 'ownershipType') {
      if (ownershipTypes.length === 0) {
        setScopeError(tx('En az bir sahiplik türü seçmelisiniz.'))
        return
      }
      includedStorageScopes = [{ kind: 'ownershipType', values: ownershipTypes }]
    } else if (scopeKind === 'quotaScope' || scopeKind === 'storageConnection') {
      const parsed: ScopeValuesResult = scopeKind === 'quotaScope' ? parseQuotaScopeLabels(quotaScopes) : parseStorageConnectionIds(connectionIds)
      if (!parsed.ok) {
        setScopeError(parsed.error)
        return
      }
      includedStorageScopes = [{ kind: scopeKind, values: parsed.values }]
    } else {
      includedStorageScopes = [{ kind: 'all' }]
    }
    // Editing the first selector must not discard additional selectors already attached to the policy.
    includedStorageScopes.push(...(policy?.includedStorageScopes?.slice(1) ?? []))

    if (isEditing) {
      update.mutate(
        {
          name: name.trim(),
          accountingMode,
          enforcementMode,
          warningThresholdPercent: Number(warningThreshold),
          criticalThresholdPercent: Number(criticalThreshold),
          enabled,
          includedStorageScopes,
        },
        { onSuccess: onClose },
      )
    } else {
      create.mutate(
        {
          key: key.trim(),
          name: name.trim(),
          accountingMode,
          enforcementMode,
          warningThresholdPercent: Number(warningThreshold),
          criticalThresholdPercent: Number(criticalThreshold),
          enabled,
          includedStorageScopes,
        },
        { onSuccess: onClose },
      )
    }
  }

  const errField = mutation.error instanceof ApiError ? mutation.error.field : undefined

  return (
    <FormDialog
      title={isEditing ? tx("Politikayı düzenle: {0}", { "0": policy?.name }) : tx('Yeni Kota Politikası')}
      description={isEditing ? tx('Değişiklikler bu politikayı kullanan tenant’ların kullanımını yeniden hesaplatabilir. Ek kapsam seçicileri korunur.') : tx('Kotaya dahil edilen depolama alanlarını, kullanım eşiklerini ve yeni Backup davranışını tanımlayın.')}
      submitLabel={isEditing ? tx('Kaydet') : tx('Oluştur')}
      pendingLabel={isEditing ? tx('Kaydediliyor…') : tx('Oluşturuluyor…')}
      pending={mutation.isPending}
      disabled={!name.trim() || (!isEditing && !key.trim())}
      error={scopeError || (mutation.error ? errorMessage(mutation.error) : undefined)}
      onSubmit={handleSubmit}
      onClose={onClose}
    >
      <fieldset className="space-y-4">
        <legend className="mb-3 text-sm font-semibold text-text-heading">{tx("Kimlik ve durum")}</legend>
        {!isEditing && <Field label={tx("Politika Anahtarı (Key)")} hint={tx("Benzersiz kimlik. Oluşturulduktan sonra değiştirilemez.")}>
          {(control) => <input {...control} className={inputClass} value={key} maxLength={64} required aria-invalid={errField === 'key'} onChange={(event) => setKey(event.target.value)} placeholder="ornek-politika-key" />}
        </Field>}
        <Field label={tx("Politika Adı")}>{(control) => <input {...control} className={inputClass} value={name} maxLength={100} required aria-invalid={errField === 'name'} onChange={(event) => setName(event.target.value)} />}</Field>
        <label className="flex min-h-8 items-center gap-2 text-sm text-text"><input className="size-4" type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />{tx("Etkin politika (planlarda seçilebilir)")}</label>
      </fieldset>
      <fieldset className="space-y-4 border-t border-border pt-4">
        <legend className="pt-4 text-sm font-semibold text-text-heading">{tx("Hesaplama ve eşikler")}</legend>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={tx("Hesaplama Modu (Accounting)")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={accountingMode} onChange={(event) => setAccountingMode(event.target.value as AccountingMode)}>
            <option value="LogicalArtifactBytes">{tx("Logical (Artifact boyutu)")}</option><option value="PhysicalReplicaBytes">{tx("Physical (Replika kopyaları dahil)")}</option>
          </select>}</Field>
          <Field label={tx("Uygulama Modu (Enforcement)")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={enforcementMode} onChange={(event) => setEnforcementMode(event.target.value as EnforcementMode)}>
            <option value="BlockNewBackups">{tx("BlockNewBackups (Yeni backup engellenir)")}</option><option value="WarnOnly">{tx("WarnOnly (Yalnızca uyar)")}</option><option value="None">{tx("None (Uygulanmaz)")}</option>
          </select>}</Field>
          <Field label={tx("Warning Eşiği (%)")}>{(control) => <input {...control} className={inputClass} type="number" min={1} max={99} value={warningThreshold} required onChange={(event) => setWarningThreshold(Number(event.target.value))} />}</Field>
          <Field label={tx("Critical Eşiği (%)")}>{(control) => <input {...control} className={inputClass} type="number" min={1} max={100} value={criticalThreshold} required onChange={(event) => setCriticalThreshold(Number(event.target.value))} />}</Field>
        </div>
      </fieldset>
      <fieldset className="space-y-4 border-t border-border pt-4">
        <legend className="pt-4 text-sm font-semibold text-text-heading">{tx("Depolama kapsamı")}</legend>
        <Field label={tx("Depolama Kapsamı")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={scopeKind} onChange={(event) => { setScopeKind(event.target.value as ScopeKind); setScopeError(null) }}>
          <option value="all">{tx("Tüm depolama alanları (All)")}</option><option value="providerType">{tx("Provider Türü (ProviderType - örn. central, local)")}</option><option value="ownershipType">{tx("Sahiplik Türü (OwnershipType - PlatformManaged / TenantManaged)")}</option><option value="quotaScope">{tx("Kota Kapsamı (QuotaScope - örn. managed, tenant)")}</option><option value="storageConnection">{tx("Depolama Bağlantısı (StorageConnection)")}</option>
        </select>}</Field>
        {scopeKind === 'all' && <p className="text-sm text-text-muted">{tx("Tüm replikalar ve depolama sağlayıcıları kotaya dahil edilir.")}</p>}
        {scopeKind === 'providerType' && <Field label={tx("Provider Anahtarları (virgülle ayırın):")} hint={<>{tx("Desteklenen sistem provider anahtarları:")}{' '}<code>central</code>, <code>local</code>.</>}>
          {(control) => <input {...control} className={inputClass} value={providerTypes} onChange={(event) => setProviderTypes(event.target.value)} placeholder="central, local" />}
        </Field>}
        {scopeKind === 'ownershipType' && <div className="space-y-2 text-sm"><p className="text-text-muted">{tx("Dahil edilecek sahiplik türleri:")}</p><div className="space-y-2">
          <label className="flex min-h-8 items-center gap-2"><input className="size-4" type="checkbox" checked={ownershipTypes.includes('PlatformManaged')} onChange={() => toggleOwnership('PlatformManaged')} />{tx("PlatformManaged (Platform yönetimli / central)")}</label>
          <label className="flex min-h-8 items-center gap-2"><input className="size-4" type="checkbox" checked={ownershipTypes.includes('TenantManaged')} onChange={() => toggleOwnership('TenantManaged')} />{tx("TenantManaged (Tenant yönetimli / local)")}</label>
        </div></div>}
        {scopeKind === 'quotaScope' && <Field label={tx("Kota kapsamı etiketleri (virgülle ayırın):")} hint={tx("Bir depolama bağlantısının kota kapsamı etiketiyle eşleşir. Küçük harf, rakam ve “-”; her etiket 1-64 karakter.")}>
          {(control) => <input {...control} className={inputClass} value={quotaScopes} onChange={(event) => setQuotaScopes(event.target.value)} placeholder="managed, tenant" aria-invalid={scopeError !== null} />}
        </Field>}
        {scopeKind === 'storageConnection' && <div className="space-y-3">
          <QuotaConnectionPicker values={connectionIds.split(/[,\n]+/).map(value => value.trim()).filter(Boolean)} onChange={(selected) => setConnectionIds(selected.join('\n'))} invalid={scopeError !== null} />
          <Field label={tx("Depolama bağlantısı kimlikleri (her satıra veya virgülle bir GUID):")}>{(control) => <textarea {...control} className={`${inputClass} h-24 font-mono`} value={connectionIds} onChange={(event) => setConnectionIds(event.target.value)} placeholder={'3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c\n11111111-2222-3333-4444-555555555555'} aria-invalid={scopeError !== null} />}</Field>
        </div>}
      </fieldset>
    </FormDialog>
  )
}
