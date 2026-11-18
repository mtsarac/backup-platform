import { tx } from '@/lib/i18n'
import type { AccountingMode, EnforcementMode, ScopeSelector } from './types'

export const accountingModeLabels: Record<AccountingMode, string> = {
  get LogicalArtifactBytes() { return tx("Logical") },
  get PhysicalReplicaBytes() { return tx("Physical") },
}

export const enforcementModeLabels: Record<EnforcementMode, string> = {
  get BlockNewBackups() { return tx("Yeni backup'ları engelle") },
  get WarnOnly() { return tx("Yalnızca uyar") },
  get None() { return tx("Kota uygulanmaz") },
}

export const ownershipLabels: Record<string, string> = { get PlatformManaged() { return tx("Platform yönetimli") }, get TenantManaged() { return tx("Tenant yönetimli") } }

export function ownershipLabel(ownership: string): string {
  return Object.hasOwn(ownershipLabels, ownership) ? ownershipLabels[ownership] : ownership
}

/** One human sentence per selector; kinds the UI does not know are shown verbatim rather than guessed at. */
export function scopeLabel(selector: ScopeSelector): string {
  const values = selector.values ?? []
  switch (selector.kind) {
    case 'all': return tx('Tüm storage (All)')
    case 'providerType': return tx("Provider türü: {0}", { "0": values.join(', ') })
    case 'ownershipType': return tx("Sahiplik: {0}", { "0": values.map(ownershipLabel).join(', ') })
    case 'quotaScope': return tx("Kota kapsamı: {0}", { "0": values.join(', ') })
    case 'storageConnection': return tx("Depolama bağlantısı: {0}", { "0": values.join(', ') })
    default: return String(selector.kind)
  }
}

export function scopeLabels(scopes: readonly ScopeSelector[]): string[] {
  return scopes.map(scopeLabel)
}
