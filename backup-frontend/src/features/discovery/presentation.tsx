import { Badge } from '@/components/ui/badge'
import { accessMeta, databaseStateMeta, fullBackupMeta, lastChangeMeta, outcomeMeta, presenceMeta, reachabilityMeta, scanStateMeta, sourceStatusMeta, type Tone } from './status'

function Pill({ meta, value, dot = true }: { meta: Record<string, { label: string; tone: Tone }>; value: string; dot?: boolean }) {
  const found = meta[value] ?? { label: value, tone: 'neutral' as const }
  return <Badge tone={found.tone} dot={dot}>{found.label}</Badge>
}
export const ReachabilityPill = ({ value }: { value: string }) => <Pill meta={reachabilityMeta} value={value} />
export const AccessPill = ({ value }: { value: string }) => <Pill meta={accessMeta} value={value} />
export const PresencePill = ({ value }: { value: string }) => <Pill meta={presenceMeta} value={value} />
export const LastChangePill = ({ value }: { value: string }) => <Pill meta={lastChangeMeta} value={value} dot={false} />
export const FullBackupPill = ({ value }: { value: string }) => <Pill meta={fullBackupMeta} value={value} />
export const DatabaseStatePill = ({ value }: { value: string }) => <Pill meta={databaseStateMeta} value={value} />
export const SourceStatusPill = ({ value }: { value: string }) => <Pill meta={sourceStatusMeta} value={value} />
export const OutcomePill = ({ value }: { value: string }) => <Pill meta={outcomeMeta} value={value} />
export const ScanStatePill = ({ value }: { value: string }) => <Pill meta={scanStateMeta} value={value} />
