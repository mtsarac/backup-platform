import { tx } from '@/lib/i18n'
import type { TechnicalItem } from '@/components/ui/technical-details'
import type { AuditDetailValue } from '@/features/platform/types'

function detailValue(value: AuditDetailValue) {
  if (value === null) return '—'
  if (typeof value === 'boolean') return value ? tx('Evet') : tx('Hayır')
  return String(value)
}

/** The raw key/value pairs an audit entry carries, as copyable technical items. */
export function auditDetailItems(details: Record<string, AuditDetailValue> | null | undefined): TechnicalItem[] {
  return Object.entries(details ?? {}).map(([label, value]) => ({ label, value: detailValue(value) }))
}

/** "Tamamlandı" / "Başarısız" for the two results the backend reports; anything else is left to the technical details. */
export function auditResultLabel(result: string | null | undefined): string {
  if (result === 'Success') return tx('Tamamlandı')
  if (result === 'Failed' || result === 'Failure') return tx('Başarısız')
  return tx('Sonuç teknik ayrıntılarda')
}
