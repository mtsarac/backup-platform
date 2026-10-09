import type { AccountingMode, EnforcementMode, EntitlementStatus, PlanSummary, QuotaState, ScopeSelector } from './types'
import { LEGACY_UNLIMITED_KEY } from './types'

type Tone = 'neutral' | 'success' | 'warning' | 'error' | 'info'

export const quotaStateMeta: Record<QuotaState, { label: string; tone: Tone }> = {
  Normal: { label: 'Normal', tone: 'success' },
  Warning: { label: 'Warning', tone: 'warning' },
  Critical: { label: 'Critical', tone: 'error' },
  OverQuota: { label: 'OverQuota', tone: 'error' },
  Unlimited: { label: 'Unlimited', tone: 'info' },
}

export const subscriptionStatusMeta: Record<EntitlementStatus, { label: string; tone: Tone }> = {
  Active: { label: 'Etkin', tone: 'success' },
  Suspended: { label: 'Askıda', tone: 'warning' },
  Expired: { label: 'Süresi doldu', tone: 'error' },
  NoSubscription: { label: 'Abonelik yok', tone: 'neutral' },
}

export const accountingModeLabels: Record<AccountingMode, string> = {
  LogicalArtifactBytes: 'Logical',
  PhysicalReplicaBytes: 'Physical',
}

export const accountingModeHelp: Record<AccountingMode, string> = {
  LogicalArtifactBytes: 'Bir backup, kaç kopyası olursa olsun kapsamdaki en az bir kopyası varsa yalnızca bir kez, kendi boyutuyla sayılır.',
  PhysicalReplicaBytes: 'Kapsamdaki her fiziksel kopya kendi boyutuyla ayrı ayrı sayılır.',
}

export const enforcementModeLabels: Record<EnforcementMode, string> = {
  BlockNewBackups: 'Yeni backup\'ları engelle',
  WarnOnly: 'Yalnızca uyar',
  None: 'Kota uygulanmaz',
}

export const enforcementModeHelp: Record<EnforcementMode, string> = {
  BlockNewBackups: 'Limite ulaşan tenant yeni backup başlatamaz.',
  WarnOnly: 'Kullanım izlenir ve gösterilir; yeni backup hiçbir zaman engellenmez.',
  None: 'Kota kararı verilmez; tenant Unlimited görünür.',
}

export const ownershipLabels: Record<string, string> = { PlatformManaged: 'Platform yönetimli', TenantManaged: 'Tenant yönetimli' }

export function ownershipLabel(ownership: string): string {
  return Object.hasOwn(ownershipLabels, ownership) ? ownershipLabels[ownership] : ownership
}

/** One human sentence per selector; kinds the UI does not know are shown verbatim rather than guessed at. */
export function scopeLabel(selector: ScopeSelector): string {
  const values = selector.values ?? []
  switch (selector.kind) {
    case 'all': return 'Tüm storage (All)'
    case 'providerType': return `Provider türü: ${values.join(', ')}`
    case 'ownershipType': return `Sahiplik: ${values.map(ownershipLabel).join(', ')}`
    case 'quotaScope': return `Kota kapsamı: ${values.join(', ')}`
    case 'storageConnection': return `Depolama bağlantısı: ${values.join(', ')}`
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
