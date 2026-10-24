import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { selectClass } from '@/components/ui/form-styles'
import { DownloadFlowStatus } from '@/features/artifacts/download-flow-status'
import { useArtifactDownload } from '@/features/artifacts/use-artifact-download'
import { failureMessage } from '@/lib/api/failure-messages'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { getSupportDownloadAvailability, getSupportDownloadStatus, prepareSupportDownload } from './api'
import { supportKeys } from './hooks'
import type { SupportScope } from './types'
import { formatBytes } from '@/lib/format'
import { QueryState } from '@/components/data/query-state'
import { errorMessage } from '@/lib/api/failure-messages'

export function SupportArtifactDownload({ scope, artifactId, eligible }: { scope: SupportScope; artifactId: string; eligible: boolean }) {
  useLanguage()
  return <DownloadController key={`${scope.sessionId}:${scope.tenantId}:${scope.enabled}:${eligible}:${artifactId}`} scope={scope} artifactId={artifactId} eligible={eligible} />
}
function DownloadController({ scope, artifactId, eligible }: { scope: SupportScope; artifactId: string; eligible: boolean }) {
  useLanguage()
  const enabled = scope.enabled && eligible
  const visible = useVisibleTab()
  const { availability, flow, replicaId, setReplicaId, selected } = useArtifactDownload({
    enabled,
    availabilityKey: supportKeys.data(scope, 'downloads', 'availability', artifactId),
    fetchAvailability: signal => getSupportDownloadAvailability(scope.sessionId, artifactId, signal),
    refetchInterval: visible ? 10_000 : false,
    prepare: replica => prepareSupportDownload(scope.sessionId, artifactId, replica),
    fetchStatus: (sessionId, signal) => getSupportDownloadStatus(scope.sessionId, artifactId, sessionId, signal),
    statusKey: sessionId => supportKeys.data(scope, 'downloads', 'status', artifactId, sessionId),
  })
  const busy = flow.busy
  const error = availability.error || flow.error
  return <Section title={tx("Download Backup")} description={tx("Dosya tarayıcınız tarafından indirilir; içerik bu arayüzde tutulmaz.")}>
    <div className="space-y-4">
    {!scope.enabled && <Notice>{tx("Etkin, sahibi olduğunuz ve DownloadBackup izni taşıyan bir destek oturumu gerekir.")}</Notice>}
    {!eligible && <Notice>{tx("Bu Artifact kullanılabilir değil; indirme kapalı.")}</Notice>}
    {enabled && <QueryState forbiddenMessage={errorMessage} isPending={availability.isPending} error={error}>{availability.data && <>
      <p className="text-sm">{formatBytes(availability.data.sizeBytes)}{' '}{tx("· Checksum:")}{' '}<code className="break-all">{availability.data.checksum}</code></p>
      <Field label={tx("Replica")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={replicaId} disabled={busy} onChange={event => setReplicaId(event.target.value)}><option value="">{tx("Otomatik · kullanılabilir Replica")}</option>{availability.data.replicas.map(replica => <option key={replica.replicaId} value={replica.replicaId} disabled={!replica.available}>{tx(replica.storageProvider)}{!replica.available ? ` · ${failureMessage(replica.reason)}` : ''}</option>)}</select>}</Field>
      {!availability.data.available && <Notice tone="warning">{tx("İndirme kullanılamıyor:")}{' '}{failureMessage(availability.data.reason)}</Notice>}
    </>}</QueryState>}
    <Button disabled={!enabled || busy || Boolean(error) || !availability.data?.available || Boolean(selected && !selected.available)} onClick={() => void flow.start()}>{busy ? tx('İndirme hazırlanıyor…') : tx('İndir')}</Button>
    <DownloadFlowStatus flow={flow} hideError />
    </div>
  </Section>
}
