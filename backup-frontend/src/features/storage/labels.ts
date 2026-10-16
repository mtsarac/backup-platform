import { tx } from '@/lib/i18n'
import type { AccountingMode, EnforcementMode, EntitlementStatus, PlanSummary, QuotaState, ScopeSelector } from './types'
import { LEGACY_UNLIMITED_KEY } from './types'

type Tone = 'neutral' | 'success' | 'warning' | 'error' | 'info'

export const quotaStateMeta: Record<QuotaState, { label: string; tone: Tone }> = {
  Normal: { label: 'Normal', tone: 'success' },
  Warning: { get label() { return tx("Warning") }, tone: 'warning' },
  Critical: { get label() { return tx("Critical") }, tone: 'error' },
  OverQuota: { get label() { return tx("OverQuota") }, tone: 'error' },
  Unlimited: { get label() { return tx("Unlimited") }, tone: 'info' },
}

export const subscriptionStatusMeta: Record<EntitlementStatus, { label: string; tone: Tone }> = {
  Active: { get label() { return tx("Etkin") }, tone: 'success' },
  Suspended: { get label() { return tx("Askıda") }, tone: 'warning' },
  Expired: { get label() { return tx("Süresi doldu") }, tone: 'error' },
  NoSubscription: { get label() { return tx("Abonelik yok") }, tone: 'neutral' },
}

export const accountingModeLabels: Record<AccountingMode, string> = {
  get LogicalArtifactBytes() { return tx("Logical") },
  get PhysicalReplicaBytes() { return tx("Physical") },
}

export const accountingModeHelp: Record<AccountingMode, string> = {
  get LogicalArtifactBytes() { return tx("Bir backup, kaç kopyası olursa olsun kapsamdaki en az bir kopyası varsa yalnızca bir kez, kendi boyutuyla sayılır.") },
  get PhysicalReplicaBytes() { return tx("Kapsamdaki her fiziksel kopya kendi boyutuyla ayrı ayrı sayılır.") },
}

export const enforcementModeLabels: Record<EnforcementMode, string> = {
  get BlockNewBackups() { return tx("Yeni backup'ları engelle") },
  get WarnOnly() { return tx("Yalnızca uyar") },
  get None() { return tx("Kota uygulanmaz") },
}

export const enforcementModeHelp: Record<EnforcementMode, string> = {
  get BlockNewBackups() { return tx("Limite ulaşan tenant yeni backup başlatamaz.") },
  get WarnOnly() { return tx("Kullanım izlenir ve gösterilir; yeni backup hiçbir zaman engellenmez.") },
  get None() { return tx("Kota kararı verilmez; tenant Unlimited görünür.") },
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

/** The system plan every pre-quota tenant was migrated onto: unlimited, no commercial limit. */
export function isLegacyUnlimited(plan: PlanSummary | null): boolean {
  return plan?.isSystem === true && plan.systemKey === LEGACY_UNLIMITED_KEY
}

export function isSubscriptionRestricted(status: EntitlementStatus): boolean {
  return status !== 'Active'
}
