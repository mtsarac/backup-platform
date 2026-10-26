import { tx, useLanguage } from '@/lib/i18n'
import type { AgentCounts } from './types'

const labels: [keyof AgentCounts, string, string][] = [
  ['online', 'Çevrimiçi', 'text-success'], ['offline', 'Çevrimdışı', 'text-text-heading'], ['unknown', 'Bağlantı bilinmiyor', 'text-text-heading'],
  ['disabled', 'Devre dışı', 'text-text-heading'], ['revoked', 'İptal edilmiş', 'text-text-heading'],
]

/** Connectivity and lifecycle counts as a row of tiles. Each `dd` directly follows its `dt`. */
export function AgentBreakdown({ counts }: { counts: AgentCounts }) {
  useLanguage()
  return <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
    <div className="rounded-lg bg-surface-2 p-3"><dt className="text-sm text-text-muted">{tx("Toplam")}</dt><dd className="text-xl font-semibold text-text-heading">{counts.total}</dd></div>
    {labels.map(([key, label, tone]) => <div key={key} className="rounded-lg bg-surface-2 p-3"><dt className="text-sm text-text-muted">{tx(label)}</dt><dd className={`text-xl font-semibold ${tone}`}>{counts[key]}</dd></div>)}
  </dl>
}
