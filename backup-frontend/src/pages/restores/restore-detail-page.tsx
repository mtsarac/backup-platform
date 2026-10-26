import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { ProgressBar } from '@/components/data/progress-bar'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { useAgents } from '@/features/agents/hooks'
import { useArtifact } from '@/features/artifacts/hooks'
import { useCancelRestore, useRestore } from '@/features/restores/hooks'
import { RollbackCopyPanel, VerificationDetail } from '@/features/restores/verification-detail'
import { canCancelRestore, databaseStateLabel, isActiveRestore, precheckLabel, restoreModeLabel, restoreProgressPercent, restoreStageLabel, safeRestoreCode, targetStateDescription, targetStateLabel, technicalChecks } from '@/features/restores/presentation'
import type { RestoreView } from '@/features/restores/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatDateTime, formatDuration } from '@/lib/format'
import { describeFailure } from '@/lib/api/failure-messages'

function RestoreProgress({ stage, percent: rawPercent, at, label }: { stage: string | null; percent: number | null; at: string | null; label: string }) {
  useLanguage()
  const percent = restoreProgressPercent(rawPercent)
  return <div className="space-y-2">
    <p className="text-sm">{restoreStageLabel(stage)} · {percent == null ? tx('Yüzde bildirilmedi') : tx('%{0}', { '0': percent.toLocaleString(currentLocale(), { maximumFractionDigits: 1 }) })}</p>
    <ProgressBar label={label} value={percent} />
    <p className="text-xs text-text-muted">{tx("Son bildirim:")}{' '}<RelativeTime value={at} /></p>
  </div>
}

const yesNo = (value?: boolean) => value ? tx('Evet') : tx('Hayır')

/** Everything a support engineer needs to quote exactly; none of it is needed to understand the restore. */
function technicalItems(restore: RestoreView): TechnicalItem[] {
  const target = `${restore.target.host}${restore.target.port ? `:${restore.target.port}` : ''}`
  return [
    { label: tx('Geri yükleme kimliği'), value: restore.id },
    { label: tx('Yedek dosyası kimliği'), value: restore.artifactId },
    { label: tx('Yedekleme bilgisayarı kimliği'), value: restore.agentId },
    { label: tx('Yedek kopyası kimliği'), value: restore.replicaId },
    ...(restore.target.host ? [{ label: tx('Hedef sunucu'), value: target }] : []),
    ...(restore.targetState ? [{ label: tx('Hedef durumu kodu'), value: restore.targetState }] : []),
    { label: tx('Kaynak erişimi'), value: restore.sourceAccess === 'AgentLocal' ? tx('Yedekleme bilgisayarındaki kopya') : tx('Merkezi depolama') },
    { label: tx('Kopya koruması'), value: restore.replicaPinned ? tx('Geri yükleme için silmeye karşı korunuyor') : tx('Geri yükleme koruması sona erdi') },
    { label: tx('Oluşturulma'), value: formatDateTime(restore.createdAt) },
    ...(restore.cancellationRequestedAt ? [{ label: tx('İptal isteği'), value: formatDateTime(restore.cancellationRequestedAt) }] : []),
    { label: tx('Deneme sayısı'), value: String(restore.attempts.length) },
    ...(restore.result ? technicalChecks(restore.result.preCheck) : []),
    ...(restore.result?.warnings.map((warning, index) => ({ label: index === 0 ? warning.code : `${warning.code} (${index + 1})`, value: warning.message || safeRestoreCode(warning.code) })) ?? []),
  ]
}

export function RestoreDetailPage() {
  useLanguage()
  const { id = '' } = useParams()
  const { tenantId } = useTenant()
  // Reset dialog/mutation state as well as query state when the route operation changes.
  return <RestoreDetail key={`${tenantId}:${id}`} id={id} />
}
function RestoreDetail({ id }: { id: string }) {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const query = useRestore(id)
  const cancel = useCancelRestore(id)
  const agents = useAgents(tenantId, true)
  const restore = query.data
  const fileRestore = restore?.options.files
  const artifact = useArtifact(tenantId, restore?.artifactId ?? '')
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [notice, setNotice] = useState('')
  const [tab, setTab] = useState<'summary' | 'details' | 'attempts' | 'checks'>('summary')
  const mayCancel = Boolean(restore && canOperate && canCancelRestore(restore.status, restore.cancellationRequestedAt))
  const computer = agents.data?.find((agent) => agent.agentId === restore?.agentId)?.hostName ?? tx('Yedekleme bilgisayarı bulunamadı')
  const sourceName = restore && (restore.sourceDatabaseName ?? restore.result?.sourceBackup?.databaseName ?? artifact.data?.databaseName ?? tx('Yedek dosyasını aç'))
  const sourceJob = restore && (restore.sourceJobName ?? artifact.data?.jobName)
  async function confirmCancel() {
    if (!mayCancel || cancel.isPending) return
    setNotice('')
    try {
      const response = await cancel.mutateAsync()
      setConfirmOpen(false)
      setNotice(response.status === 'Cancelled' && !fileRestore ? tx('Geri yükleme iptal edildi. Hedef veritabanının durumunu ayrıca kontrol edin.') : tx('İptal isteği iletildi; yedekleme bilgisayarı sonucu bildirmeden geri yükleme iptal edilmiş sayılmaz.'))
    } catch {
      // The mutation error is mapped to a safe message in the confirmation dialog.
    }
  }
  const source = restore && <Fact label={tx("Kaynak yedek")}><Link className={linkClass} to={`/tenants/${tenantId}/artifacts/${restore.artifactId}`}>{sourceName}</Link>
    {sourceJob && <p className="mt-1 text-sm text-text-muted">{sourceJob}</p>}
    {artifact.error && <p className="mt-1 text-sm text-text-muted">{tx("Yedek dosyasının ayrıntıları yüklenemedi; geri yükleme kaydı yine de kullanılabilir.")}</p>}
  </Fact>
  return <div className="space-y-6">
    <QueryState isPending={query.isPending} error={query.error} empty={!query.isPending && !restore} onRetry={() => void query.refetch()}>
      {restore && <>
        <PageHeader title={`${restore.purpose === 'Verification' ? tx('Geri yükleme testi') : restore.purpose === 'Precheck' ? tx('Geri yükleme ön kontrolü') : restore.purpose === 'Discovery' ? tx('Hedef veritabanı bulma') : tx('Geri yükleme')} · ${restore.databaseName}`}
          crumbs={[{ label: tx('Geri yükleme geçmişi'), to: `/tenants/${tenantId}/restores` }, { label: restore.databaseName }]}
          description={fileRestore ? tx('Dosya / klasör geri yükleme') : restoreModeLabel(restore.mode)}
          meta={<><StatusPill status={restore.status} /><span className="text-sm text-text-muted"><RelativeTime value={restore.createdAt} /></span></>}
          actions={mayCancel && <Button variant="destructive" disabled={cancel.isPending} onClick={() => { cancel.reset(); setConfirmOpen(true) }}>{tx("Geri yüklemeyi iptal et")}</Button>} />
        {notice && (isActiveRestore(restore.status) || restore.status === 'Cancelled') && <Notice>{notice}</Notice>}
        {restore.cancellationRequestedAt && isActiveRestore(restore.status) && <Notice tone="warning">{tx("İptal istendi; işlem henüz sona ermedi. Yedekleme bilgisayarının sonucu ve veritabanının durumu bekleniyor.")}</Notice>}
        {['Failed', 'Cancelled', 'AgentLost'].includes(restore.status) && restore.purpose !== 'Verification' && <section aria-label={tx("Geri yükleme sonucu uyarısı")}><ErrorNotice
          title={fileRestore ? tx('Hedef dosyaları kontrol edin') : tx("Hedef veritabanını kontrol edin")}
          {...describeFailure(restore.failureCode, 'restore')}
          {...(restore.status === 'Failed' && restore.failureCode ? {} : { what: restore.status === 'AgentLost' ? tx('Yedekleme bilgisayarıyla bağlantı koptu.') : tx('Geri yükleme iptal edildi.') })}
          dataImpact={fileRestore ? tx('Başarısız veya iptal edilmiş işlemden sonra hedef dosyaları ve varsa korunan geri dönüş verilerini kontrol edin; elle müdahale gerekebilir.') : tx("Bu durum veritabanının sağlıklı olduğunu göstermez. Hedef veritabanının durumunu kontrol edin; elle müdahale gerekebilir.")} /></section>}
        <Tabs label={tx("Geri yükleme bölümleri")} idPrefix="restore" value={tab} onChange={setTab} items={[
          { value: 'summary', label: tx('Özet') }, { value: 'details', label: tx('Kaynak ve seçenekler') }, { value: 'attempts', label: tx('Denemeler') }, { value: 'checks', label: tx('Doğrulama sonucu') },
        ]} />
        <div role="tabpanel" id={`restore-panel-${tab}`} aria-labelledby={`restore-tab-${tab}`} className="space-y-6">
          {tab === 'summary' && <>
            {restore.purpose === 'Verification' && <VerificationDetail restore={restore} />}
            <RollbackCopyPanel restore={restore} />
            {restore.purpose !== 'Verification' && <Section title={fileRestore ? tx('Hedef dosyaların durumu') : tx("Hedef veritabanının durumu")} description={fileRestore ? undefined : targetStateDescription(restore.targetState)}>
              <p className="font-medium text-text-heading">{targetStateLabel(restore.targetState)}</p>
              {restore.result && !fileRestore && <p className="mt-2 text-sm">{tx("Veritabanı durumu:")}{' '}{databaseStateLabel(restore.result.databaseState)}</p>}
            </Section>}
            <Section title={tx("İlerleme")}><RestoreProgress stage={restore.progress.stage} percent={restore.progress.percent} at={restore.progress.at} label={tx("Geri yükleme ilerlemesi")} /></Section>
            <Section title={tx("Geri yükleme özeti")}><Facts columns={3}>
              <Fact label={fileRestore ? tx('Hedef dosya kaynağı referansı') : tx("Hedef veritabanı")}>{restore.databaseName}</Fact>
              <Fact label={tx("Mod")}>{fileRestore ? restore.mode === 'ReplaceExistingDatabase' ? tx('Mevcut dosyaların üzerine yazmaya izin ver') : tx('Mevcut dosyaların üzerine yazılmaz') : restoreModeLabel(restore.mode)}</Fact>
              {source}
              <Fact label={tx("Yedekleme bilgisayarı")}>{computer}</Fact>
              <Fact label={tx("Başlangıç")}><RelativeTime value={restore.startedAt} /></Fact>
              <Fact label={tx("Bitiş")}><RelativeTime value={restore.completedAt} /></Fact>
              {restore.completedAt && <Fact label={tx("Süre")}>{formatDuration(restore.startedAt, restore.completedAt)}</Fact>}
            </Facts></Section>
          </>}
          {tab === 'details' && <Section title={tx("Kaynak ve seçenekler")}><Facts>
            {source}
            <Fact label={tx("Yedekleme bilgisayarı")}>{computer}</Fact>
            <Fact label={tx("Dosya bütünlüğü kontrolü istendi")}>{yesNo(restore.options.verifyChecksum)}</Fact>
            <Fact label={tx("Yedek doğrulaması istendi")}>{yesNo(restore.options.verifyBackup)}</Fact>
            {fileRestore ? <>
              <Fact label={tx('Hedef göreli klasör')}>{fileRestore.relativeDirectory || '.'}</Fact>
              <Fact label={tx('Geri yükleme kapsamı')}>{fileRestore.paths?.join(', ') ?? tx('Tüm dosya ve klasörler')}</Fact>
            </> : <Fact label={tx("Aktif bağlantıları sonlandır")}>{yesNo(restore.options.terminateActiveConnections)}</Fact>}
          </Facts></Section>}
          {tab === 'attempts' && <Section title={tx("Denemeler")}>
            {restore.attempts.length === 0 ? <EmptyState message={tx("Henüz deneme yok; yedekleme bilgisayarı görevi alınca deneme bilgileri burada görünür.")} /> : <ol className="divide-y divide-border">{restore.attempts.map((attempt) => <li key={attempt.id} className="py-5 first:pt-0 last:pb-0"><article aria-label={tx("{0}. deneme", { "0": attempt.number })} className="space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="font-medium">{attempt.number}{tx(". deneme")}</h3><StatusPill status={attempt.status} /></div>
              <Facts columns={3}>
                <Fact label={tx('Oluşturulma')}><RelativeTime value={attempt.createdAt} /></Fact>
                <Fact label={tx('Yedekleme bilgisayarı aldı')}><RelativeTime value={attempt.leasedAt} /></Fact>
                <Fact label={tx('Başlangıç')}><RelativeTime value={attempt.startedAt} /></Fact>
                <Fact label={tx('Bitiş')}><RelativeTime value={attempt.completedAt} /></Fact>
                <Fact label={tx('Hedef veritabanının durumu')}>{targetStateLabel(attempt.targetState)}</Fact>
              </Facts>
              <RestoreProgress stage={attempt.progressStage} percent={attempt.progressPercent} at={attempt.progressAt} label={tx("{0}. deneme ilerlemesi", { "0": attempt.number })} />
              {attempt.failureCode && <ErrorNotice failureCode={attempt.failureCode} scope="restore" />}
            </article></li>)}</ol>}
          </Section>}
          {tab === 'checks' && <Section title={tx("Doğrulama sonucu")}>
            {restore.result ? <div className="space-y-4">
              <Facts><Fact label={tx("Dosya bütünlüğü doğrulandı")}>{yesNo(restore.result.checksumVerified)}</Fact><Fact label={tx("Yedek doğrulandı")}>{yesNo(restore.result.backupVerified)}</Fact></Facts>
              <ul className="divide-y divide-border text-sm">{restore.result.preCheck.map((check, index) => <li key={`${check.checkId}:${index}`} className="flex flex-wrap justify-between gap-2 py-3"><span className="min-w-0 break-words">{precheckLabel(check.checkId)}</span><span className="font-medium">{({ Passed: tx('Geçti'), Warning: tx('Uyarı'), Failed: tx('Başarısız'), Skipped: tx('Atlandı') })[check.status]}</span></li>)}</ul>
              {restore.result.warnings.length > 0 && <Notice tone="warning">{tx("Geri yükleme sırasında uyarılar bildirildi; ayrıntıları aşağıdaki teknik ayrıntılarda bulabilirsiniz.")}</Notice>}
            </div> : <EmptyState message={tx("Doğrulama sonucu henüz bildirilmedi; yedekleme bilgisayarı sonucu gönderince kontrol ayrıntıları burada görünür.")} />}
          </Section>}
        </div>
        <TechnicalDetails items={technicalItems(restore)} />
      </>}
    </QueryState>
    <ConfirmDialog open={confirmOpen && mayCancel} onClose={() => setConfirmOpen(false)} title={tx("Geri yükleme iptal edilsin mi?")}
      consequence={{
        what: fileRestore
          ? tx('Başarısız veya iptal edilmiş işlemden sonra hedef dosyaları ve varsa korunan geri dönüş verilerini kontrol etmeniz gerekebilir.')
          : tx("İptal isteği yedekleme bilgisayarına iletilir; veritabanı motoru işlemi anında durdurmayabilir. Başlamış bir geri yükleme hedef veritabanını kısmen geri yüklenmiş veya bilinmeyen durumda bırakabilir."),
        who: tx("Hedef veritabanı etkilenir. İptal, veritabanının sağlıklı olduğunu garanti etmez."),
        reversible: tx("Hayır. Elle müdahale gerekebilir; geri yüklemeyi yeniden başlatabilirsiniz."),
      }}
      confirmLabel={tx("İptali onayla")} pending={cancel.isPending} error={cancel.error} errorScope="restore" onConfirm={() => void confirmCancel()} />
  </div>
}
