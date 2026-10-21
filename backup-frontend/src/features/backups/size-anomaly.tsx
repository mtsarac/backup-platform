import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { artifactSize } from '@/features/artifacts/format'
import type { BackupRun } from './types'

export function SizeAnomalyBadge({ run }: { run: BackupRun }) {
  useLanguage()
  if (run.sizeAnomalyStatus === 'SizeAnomalyWarning') return <Badge tone="warning">{tx('Boyut Anomalisi')}</Badge>
  if (run.sizeAnomalyStatus === 'BaselinePending') return <Badge tone="neutral">{tx('Boyut baz çizgisi bekleniyor')}</Badge>
  return null
}

export function SizeAnomalyComparison({ run }: { run: BackupRun }) {
  useLanguage()
  if (run.sizeAnomalyBaselineBytes == null || run.sizeAnomalyDeviationRatio == null) return null
  const deviation = run.sizeAnomalyDeviationRatio.toLocaleString(currentLocale(), { style: 'percent', maximumFractionDigits: 1, signDisplay: 'always' })
  return <p className="whitespace-normal text-sm text-text-muted">{tx('Geçmiş ortalama: {0} · Sapma: {1}', { '0': artifactSize(run.sizeAnomalyBaselineBytes), '1': deviation })}</p>
}
