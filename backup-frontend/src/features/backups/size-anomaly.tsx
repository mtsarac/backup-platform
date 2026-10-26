import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import type { BackupRun } from './types'
import { formatBytes } from '@/lib/format'

export function SizeAnomalyBadge({ run }: { run: BackupRun }) {
  useLanguage()
  if (run.sizeAnomalyStatus === 'SizeAnomalyWarning') return <StatusPill level="attention">{tx('Boyut Anomalisi')}</StatusPill>
  if (run.sizeAnomalyStatus === 'BaselinePending') return <StatusPill level="pending">{tx('Boyut baz çizgisi bekleniyor')}</StatusPill>
  return null
}

export function SizeAnomalyComparison({ run }: { run: BackupRun }) {
  useLanguage()
  if (run.sizeAnomalyBaselineBytes == null || run.sizeAnomalyDeviationRatio == null) return null
  const deviation = run.sizeAnomalyDeviationRatio.toLocaleString(currentLocale(), { style: 'percent', maximumFractionDigits: 1, signDisplay: 'always' })
  return <p className="whitespace-normal text-sm text-text-muted">{tx('Geçmiş ortalama: {0} · Sapma: {1}', { '0': formatBytes(run.sizeAnomalyBaselineBytes), '1': deviation })}</p>
}
