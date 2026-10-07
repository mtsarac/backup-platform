import type { AgentCounts } from './types'

const labels: [keyof AgentCounts, string][] = [
  ['online', 'Çevrimiçi'], ['offline', 'Çevrimdışı'], ['unknown', 'Bağlantı bilinmiyor'], ['disabled', 'Devre dışı'], ['revoked', 'İptal edilmiş'],
]

export function AgentBreakdown({ counts }: { counts: AgentCounts }) {
  return <dl className="flex flex-wrap gap-5 text-sm">
    <div><dt className="text-text-muted">Toplam Agent</dt><dd className="font-semibold">{counts.total}</dd></div>
    {labels.map(([key, label]) => <div key={key}><dt className="text-text-muted">{label}</dt><dd className="font-semibold">{counts[key]}</dd></div>)}
  </dl>
}
