import { useState } from 'react'
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
        setScopeError('En az bir provider türü girmelisiniz (örn. central, local).')
        return
      }
      includedStorageScopes = [{ kind: 'providerType', values: providers }]
    } else if (scopeKind === 'ownershipType') {
      if (ownershipTypes.length === 0) {
        setScopeError('En az bir sahiplik türü seçmelisiniz.')
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
      title={isEditing ? `Politikayı düzenle: ${policy?.name}` : 'Yeni Kota Politikası'}
      submitLabel={isEditing ? 'Kaydet' : 'Oluştur'}
      pendingLabel={isEditing ? 'Kaydediliyor…' : 'Oluşturuluyor…'}
      pending={mutation.isPending}
      disabled={!name.trim() || (!isEditing && !key.trim())}
      error={scopeError || (mutation.error ? errorMessage(mutation.error) : undefined)}
      onSubmit={handleSubmit}
      onClose={onClose}
    >
      {!isEditing && (
        <label className="block text-sm text-text-muted">
          Politika Anahtarı (Key)
          <input
            className={inputClass}
            value={key}
            maxLength={64}
            required
            aria-invalid={errField === 'key'}
            onChange={(e) => setKey(e.target.value)}
            placeholder="ornek-politika-key"
          />
          <span className="mt-1 block text-xs">Benzersiz kimlik. Oluşturulduktan sonra değiştirilemez.</span>
        </label>
      )}

      <label className="block text-sm text-text-muted">
        Politika Adı
        <input
          className={inputClass}
          value={name}
          maxLength={100}
          required
          aria-invalid={errField === 'name'}
          onChange={(e) => setName(e.target.value)}
        />
      </label>

      <div className="grid gap-3 sm:grid-cols-2">
        <label className="block text-sm text-text-muted">
          Hesaplama Modu (Accounting)
          <select
            className={selectClass}
            value={accountingMode}
            onChange={(e) => setAccountingMode(e.target.value as AccountingMode)}
          >
            <option value="LogicalArtifactBytes">Logical (Artifact boyutu)</option>
            <option value="PhysicalReplicaBytes">Physical (Replika kopyaları dahil)</option>
          </select>
        </label>

        <label className="block text-sm text-text-muted">
          Uygulama Modu (Enforcement)
          <select
            className={selectClass}
            value={enforcementMode}
            onChange={(e) => setEnforcementMode(e.target.value as EnforcementMode)}
          >
            <option value="BlockNewBackups">BlockNewBackups (Yeni backup engellenir)</option>
            <option value="WarnOnly">WarnOnly (Yalnızca uyar)</option>
            <option value="None">None (Uygulanmaz)</option>
          </select>
        </label>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <label className="block text-sm text-text-muted">
          Warning Eşiği (%)
          <input
            className={inputClass}
            type="number"
            min={1}
            max={99}
            value={warningThreshold}
            required
            onChange={(e) => setWarningThreshold(Number(e.target.value))}
          />
        </label>

        <label className="block text-sm text-text-muted">
          Critical Eşiği (%)
          <input
            className={inputClass}
            type="number"
            min={1}
            max={100}
            value={criticalThreshold}
            required
            onChange={(e) => setCriticalThreshold(Number(e.target.value))}
          />
        </label>
      </div>

      <div className="space-y-2 rounded-lg border border-border bg-surface-2 p-3">
        <label className="block text-sm font-medium text-text-heading">
          Depolama Kapsamı (Included Storage Scopes)
          <select
            className={`mt-1 ${selectClass}`}
            value={scopeKind}
            onChange={(e) => {
              setScopeKind(e.target.value as ScopeKind)
              setScopeError(null)
            }}
          >
            <option value="all">Tüm depolama alanları (All)</option>
            <option value="providerType">Provider Türü (ProviderType - örn. central, local)</option>
            <option value="ownershipType">Sahiplik Türü (OwnershipType - PlatformManaged / TenantManaged)</option>
            <option value="quotaScope">Kota Kapsamı (QuotaScope - örn. managed, tenant)</option>
            <option value="storageConnection">Depolama Bağlantısı (StorageConnection)</option>
          </select>
        </label>

        {scopeKind === 'all' && (
          <p className="text-xs text-text-muted">
            Tüm replikalar ve depolama sağlayıcıları kotaya dahil edilir.
          </p>
        )}

        {scopeKind === 'providerType' && (
          <label className="block text-xs text-text-muted">
            Provider Anahtarları (virgülle ayırın):
            <input
              className={`mt-1 ${inputClass}`}
              value={providerTypes}
              onChange={(e) => setProviderTypes(e.target.value)}
              placeholder="central, local"
            />
            <span className="mt-1 block text-[11px] text-text-muted">
              Desteklenen sistem provider anahtarları: <code>central</code>, <code>local</code>.
            </span>
          </label>
        )}

        {scopeKind === 'ownershipType' && (
          <div className="space-y-1 text-xs">
            <span className="text-text-muted">Dahil edilecek sahiplik türleri:</span>
            <div className="flex flex-wrap gap-4 pt-1">
              <label className="flex items-center gap-1.5 text-text">
                <input
                  type="checkbox"
                  checked={ownershipTypes.includes('PlatformManaged')}
                  onChange={() => toggleOwnership('PlatformManaged')}
                />
                PlatformManaged (Platform yönetimli / central)
              </label>
              <label className="flex items-center gap-1.5 text-text">
                <input
                  type="checkbox"
                  checked={ownershipTypes.includes('TenantManaged')}
                  onChange={() => toggleOwnership('TenantManaged')}
                />
                TenantManaged (Tenant yönetimli / local)
              </label>
            </div>
          </div>
        )}

        {scopeKind === 'quotaScope' && (
          <label className="block text-xs text-text-muted">
            Kota kapsamı etiketleri (virgülle ayırın):
            <input
              className={`mt-1 ${inputClass}`}
              value={quotaScopes}
              onChange={(e) => setQuotaScopes(e.target.value)}
              placeholder="managed, tenant"
              aria-invalid={scopeError !== null}
            />
            <span className="mt-1 block text-[11px] text-text-muted">
              Bir depolama bağlantısının kota kapsamı etiketiyle eşleşir. Küçük harf, rakam ve “-”; her etiket 1-64 karakter.
            </span>
          </label>
        )}

        {scopeKind === 'storageConnection' && (
          <div className="space-y-2">
            <label className="block text-xs text-text-muted">
              Depolama bağlantısı kimlikleri (her satıra veya virgülle bir GUID):
              <textarea
                className={`mt-1 h-20 font-mono ${inputClass}`}
                value={connectionIds}
                onChange={(e) => setConnectionIds(e.target.value)}
                placeholder={'3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c\n11111111-2222-3333-4444-555555555555'}
                aria-invalid={scopeError !== null}
              />
            </label>
            <QuotaConnectionPicker
              values={connectionIds.split(/[,\n]+/).map(v => v.trim()).filter(Boolean)}
              onChange={(selected) => setConnectionIds(selected.join('\n'))}
            />
          </div>
        )}
      </div>

      <label className="flex items-center gap-2 text-sm text-text">
        <input
          type="checkbox"
          checked={enabled}
          onChange={(e) => setEnabled(e.target.checked)}
        />
        Etkin politika (planlarda seçilebilir)
      </label>
    </FormDialog>
  )
}
