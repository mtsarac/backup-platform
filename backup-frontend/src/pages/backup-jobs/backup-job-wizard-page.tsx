import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getAgents } from '@/features/agents/api'
import { useBackupJob, useJobOccurrences, useSaveBackupJob } from '@/features/backup-jobs/hooks'
import { Advanced, FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit } from '@/features/backup-jobs/job-field-kit'
import { JobSummary } from '@/features/backup-jobs/job-summary'
import { jobSteps, schedulePresets, stepOfField, summaryStep, validateStep } from '@/features/backup-jobs/job-wizard-model'
import { SourceStep } from '@/features/backup-jobs/source-step'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { connectionUnavailableReason, primaryDestination, resolveDestinationConnection } from '@/features/storage-connections/job-connection-picker-utils'
import { MultiDestinationPicker } from '@/features/storage-connections/multi-destination-picker'
import type { BackupJob, BackupJobInput, BackupConfiguration, Schedule, RetryPolicy, RetentionPolicy } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'

const initialConfiguration: BackupConfiguration = { engine: 'mssql', database: '', host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }], requiredVerifiedReplicaCount: 1, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred' }
const initialSchedule: Schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 3600, overlapPolicy: 'Skip' }
const initialRetry: RetryPolicy = { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 }
const initialRetention: RetentionPolicy = { enabled: true, keepForSeconds: 1209600, deleteGracePeriodSeconds: 86400 }
const policies = [['Disabled', 'Off — kapalı'], ['Preferred', 'IfSupported — destekleniyorsa'], ['Required', 'Required — zorunlu']] as const

export function BackupJobWizardPage() {
  const { id = '' } = useParams()
  const { tenantId, canOperate } = useTenant()
  const job = useBackupJob(id)
  const crumbs = [{ label: 'Yedekleme işleri', to: `/tenants/${tenantId}/backup-jobs` }, { label: id ? 'Düzenle' : 'Yeni iş' }]
  let body
  if (!canOperate) body = <Notice tone="error">Bu sayfada değişiklik yapma yetkiniz yok. Viewer rolü salt okunurdur.</Notice>
  else if (id) body = <QueryState isPending={job.isPending} error={job.error}>{job.data && <JobForm key={`${tenantId}:${id}`} job={job.data} />}</QueryState>
  else body = <JobForm key={`${tenantId}:new`} />
  return <div className="space-y-6">
    <PageHeader crumbs={crumbs} title={id ? 'Yedekleme işini düzenle' : 'Yeni yedekleme işi'} description="Kaynak, depolama, zamanlama ve saklama ayarlarını adım adım belirleyin." />
    {body}
  </div>
}

function JobForm({ job }: { job?: BackupJob }) {
  const { tenantId, canAdmin } = useTenant()
  const navigate = useNavigate()
  const save = useSaveBackupJob(job?.id)
  const occurrences = useJobOccurrences(job?.id ?? '')
  const agents = useQuery({ queryKey: ['tenant', tenantId, 'agents'], queryFn: ({ signal }) => getAgents(tenantId, signal) })
  const [name, setName] = useState(job?.name ?? '')
  const [agentId, setAgentId] = useState(job?.agentId ?? '')
  const [configuration, setConfiguration] = useState(job?.configuration ?? initialConfiguration)
  const [retry, setRetry] = useState(job?.retryPolicy ?? initialRetry)
  const [retention, setRetention] = useState(job?.retention ?? initialRetention)
  const [schedule, setSchedule] = useState(job?.schedule ?? initialSchedule)
  const [scheduled, setScheduled] = useState(job ? Boolean(job.schedule) : true)
  const [enabled, setEnabled] = useState(job?.enabled ?? true)
  const [overlap, setOverlap] = useState<'Skip' | 'Queue'>(job?.overlapPolicy ?? 'Skip')
  const [step, setStep] = useState(job ? summaryStep : 0)
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({})
  const saving = useRef(false)
  const mounted = useRef(false)
  const formRef = useRef<HTMLFormElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const connections = useTenantConnections()
  const providers = useTenantProviders()
  const connectionItems = connections.data?.items ?? []
  const providerItems = providers.data?.items ?? []
  const primary = primaryDestination(configuration)
  const target = resolveDestinationConnection(primary, connectionItems)
  const targetProvider = providerItems.find(item => item.providerType === target?.providerType)
  const savedPrimary = job && primaryDestination(job.configuration)
  const existingTarget = Boolean(savedPrimary && (savedPrimary.storageConnectionId ?? null) === (primary.storageConnectionId ?? null) && savedPrimary.storageProvider === primary.storageProvider)
  const pickerPending = connections.isPending || providers.isPending
  const pickerError = connections.error ?? providers.error
  const targetBlocked = !existingTarget && Boolean(connectionUnavailableReason(target, targetProvider))
  const cannotSave = pickerPending || Boolean(pickerError) || targetBlocked
  function refreshConnections() {
    void connections.refetch()
    void providers.refetch()
  }
  const serverErrors: Record<string, string> = save.error instanceof ApiError ? { ...Object.fromEntries(Object.entries(save.error.fields).map(([field, code]) => [field.replace(/^schedule\./, ''), failureMessage(code)])), ...(save.error.field ? { [save.error.field.replace(/^schedule\./, '')]: errorMessage(save.error) } : {}) } : {}
  const errors = { ...serverErrors, ...clientErrors }
  const errorSteps = [...new Set(Object.keys(serverErrors).map(stepOfField).filter((value): value is number => value !== undefined))].sort((a, b) => a - b)
  const { text, number, check, select } = fieldKit(errors)
  const withErrors = (...names: string[]) => names.some(item => errors[item])
  const nextOccurrence = job && (job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt)
  const nextRun = job ? nextOccurrence ? new Date(nextOccurrence).toLocaleString('tr-TR') : 'Planlanmamış' : undefined
  const agent = agents.data?.find(item => item.agentId === agentId)

  useEffect(() => {
    if (!mounted.current) { mounted.current = true; return }
    headingRef.current?.focus()
  }, [step])
  useEffect(() => {
    if (Object.keys(clientErrors).length) formRef.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus()
  }, [clientErrors])

  function go(target: number) {
    setClientErrors({})
    setStep(target)
  }
  function advance(target: number) {
    const found = validateStep(step, { name, agentId, configuration, scheduled, schedule, retry })
    if (Object.keys(found).length) setClientErrors(found)
    else go(target)
  }
  function submit() {
    if (saving.current || cannotSave) return
    saving.current = true
    const input: BackupJobInput = { name, agentId, configuration, retryPolicy: retry, overlapPolicy: overlap, enabled, schedule: scheduled ? { ...schedule, overlapPolicy: overlap } : null, ...(canAdmin ? { retention } : {}) }
    save.mutate(input, {
      onSuccess: () => navigate(`/tenants/${tenantId}/backup-jobs`),
      onError: error => {
        if (error instanceof ApiError && ['storage_connection_disabled', 'storage_connection_unavailable'].includes(error.reason ?? error.code ?? '')) refreshConnections()
      },
      onSettled: () => { saving.current = false },
    })
  }
  const last = step === summaryStep
  return <form ref={formRef} noValidate className="space-y-5" onSubmit={event => {
    event.preventDefault()
    if (last) submit()
    else advance(step + 1)
  }}>
    <Stepper steps={jobSteps} current={step} />
    <h2 ref={headingRef} tabIndex={-1} className="sr-only">Adım {step + 1} / {jobSteps.length}: {jobSteps[step]}</h2>
    {step === 0 && <Section title="İş ve Agent" description="İşin adı ve yedeği alacak Agent.">
      <FormGrid>
        {text('name', 'İş adı', name, setName)}
        <div className="min-w-0 space-y-1">
          {select('agentId', 'Agent', agentId, [['', 'Agent seçin'], ...(agents.data ?? []).map(item => [item.agentId, `${item.hostName ?? 'Adsız Agent'} · ${item.operationalStatus}`] as const)], setAgentId)}
          {agents.isPending && <p role="status" className="text-sm text-text-muted">Agent listesi yükleniyor…</p>}{agents.error && <p role="alert" className="text-sm text-error">{errorMessage(agents.error)}</p>}
        </div>
        {check('İş etkin', enabled, setEnabled)}
      </FormGrid>
    </Section>}
    {step === 1 && <SourceStep key={configuration.engine} configuration={configuration} errors={errors} onChange={setConfiguration} />}
    {step === 2 && <>
      <Section title="Depolama ve yürütme" description="Yedeğin yazılacağı hedefler.">
        <MultiDestinationPicker configuration={configuration} connections={connectionItems} providers={providerItems} pending={pickerPending} error={pickerError} fieldError={Object.entries(errors).find(([field]) => field.startsWith('configuration.destinations'))?.[1] ?? errors['configuration.requiredVerifiedReplicaCount']} existingTarget={existingTarget} onChange={setConfiguration} onRefresh={refreshConnections} />
      </Section>
      <Advanced title="Depolama ayrıntıları" open={withErrors('configuration.storagePrefix', 'configuration.stagingReference')}>
        {text('configuration.storagePrefix', 'Hedef öneki (göreli anahtar)', configuration.storagePrefix, storagePrefix => setConfiguration({ ...configuration, storagePrefix }))}
        {text('configuration.stagingReference', 'Geçici alan referansı (staging)', configuration.stagingReference, stagingReference => setConfiguration({ ...configuration, stagingReference }))}
      </Advanced>
    </>}
    {step === 3 && <>
      <Section title="Zamanlama" description="Manuel çalıştırma her zaman mümkündür; zamanlama isteğe bağlıdır.">
        <FormGrid>
          {check('Zamanlama kullan', scheduled, setScheduled)}
          {scheduled && check('Zamanlama etkin', schedule.enabled, value => setSchedule({ ...schedule, enabled: value }))}
          {scheduled && <div role="group" aria-label="Hazır zamanlamalar" className="flex flex-wrap gap-2 md:col-span-2">{schedulePresets.map(([label, cron]) => <Button key={cron} type="button" variant={schedule.cronExpression === cron ? 'default' : 'outline'} aria-pressed={schedule.cronExpression === cron} onClick={() => setSchedule({ ...schedule, cronExpression: cron })}>{label}</Button>)}</div>}
          {nextRun && <p className="text-sm text-text-muted md:col-span-2">Kaydedilmiş zamanlamanın sonraki çalışması: {nextRun}. Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.</p>}
        </FormGrid>
      </Section>
      <Advanced title="Gelişmiş zamanlama" open={withErrors('cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy')}>
        {scheduled && text('cronExpression', 'Cron ifadesi', schedule.cronExpression, cronExpression => setSchedule({ ...schedule, cronExpression }))}
        {scheduled && text('timeZone', 'Saat dilimi (IANA / sistem adı)', schedule.timeZone, timeZone => setSchedule({ ...schedule, timeZone }))}
        {scheduled && number('missedRunGraceSeconds', 'Kaçırılan çalışma toleransı (saniye)', schedule.missedRunGraceSeconds, value => setSchedule({ ...schedule, missedRunGraceSeconds: value ?? 0 }))}
        {select('overlapPolicy', 'Önceki çalışma sürerken yeni zamanlama', overlap, [['Skip', 'Atla'], ['Queue', 'Kuyruğa al']], value => setOverlap(value as 'Skip' | 'Queue'))}
      </Advanced>
      <Advanced title="Yeniden deneme" open={withErrors('retryPolicy.maxAttempts', 'retryPolicy.initialDelaySeconds', 'retryPolicy.maxDelaySeconds', 'retryPolicy.backoff', 'retryPolicy.jitter')}>
        {number('retryPolicy.maxAttempts', 'Toplam deneme sayısı', retry.maxAttempts, value => setRetry({ ...retry, maxAttempts: value ?? 0 }))}
        {number('retryPolicy.initialDelaySeconds', 'İlk gecikme (saniye)', retry.initialDelaySeconds, value => setRetry({ ...retry, initialDelaySeconds: value ?? 0 }))}
        {number('retryPolicy.maxDelaySeconds', 'En uzun gecikme (saniye)', retry.maxDelaySeconds, value => setRetry({ ...retry, maxDelaySeconds: value ?? 0 }))}
        {select('retryPolicy.backoff', 'Gecikme stratejisi', retry.backoff, [['Fixed', 'Fixed — sabit'], ['Exponential', 'Exponential — üstel']], backoff => setRetry({ ...retry, backoff }))}
        {number('retryPolicy.jitter', 'Rastgele gecikme oranı (jitter)', retry.jitter, value => setRetry({ ...retry, jitter: value ?? 0 }))}
      </Advanced>
    </>}
    {step === 4 && <>
      <Section title="Saklama politikası" description="Yedeklerin ne kadar süre saklanacağı.">
        <FormGrid>
          {!canAdmin && <p className="text-sm text-text-muted md:col-span-2">Saklama politikasını yalnızca Owner / Admin değiştirebilir. {job ? 'Mevcut politika korunur.' : 'Sunucunun varsayılan politikası uygulanır.'}</p>}
          {check('Saklama süresi etkin', retention.enabled, value => setRetention({ ...retention, enabled: value }), !canAdmin)}
          {number('retention.keepForSeconds', 'KeepFor — saklama süresi (saniye)', retention.keepForSeconds, value => setRetention({ ...retention, keepForSeconds: value ?? 0 }), !canAdmin)}
          {number('retention.deleteGracePeriodSeconds', 'DeleteGracePeriod — silme bekleme süresi (saniye)', retention.deleteGracePeriodSeconds, value => setRetention({ ...retention, deleteGracePeriodSeconds: value ?? 0 }), !canAdmin)}
          <p className="text-sm text-text-muted md:col-span-2">Politika değişikliği mevcut yedeklerin son kullanma tarihini değiştirmez.</p>
        </FormGrid>
      </Section>
      <Section title="Yedekleme seçenekleri">
        <FormGrid>
          {check('Kopya yedek (COPY_ONLY: mevcut yedek zincirini etkilemez)', configuration.copyOnly, copyOnly => setConfiguration({ ...configuration, copyOnly }))}
          {select('configuration.checksumPolicy', 'Bütünlük doğrulaması (CHECKSUM)', configuration.checksumPolicy, policies, value => setConfiguration({ ...configuration, checksumPolicy: value as BackupConfiguration['checksumPolicy'] }))}
          {select('configuration.compressionPolicy', 'Sıkıştırma (Compression)', configuration.compressionPolicy, policies, value => setConfiguration({ ...configuration, compressionPolicy: value as BackupConfiguration['compressionPolicy'] }))}
        </FormGrid>
      </Section>
    </>}
    {last && <>
      <JobSummary name={name} agentLabel={agent ? `${agent.hostName ?? 'Adsız Agent'} · ${agent.operationalStatus}` : agentId ? 'Agent bulunamadı' : 'Seçilmedi'} enabled={enabled} configuration={configuration} scheduled={scheduled} schedule={schedule} overlap={overlap} retry={retry} retention={retention} canAdmin={canAdmin} connections={connectionItems} nextRun={nextRun} onGo={go} />
      {job && <Section title="Son zamanlama oluşumları"><QueryState isPending={occurrences.isPending} error={occurrences.error} empty={occurrences.data?.length === 0}><ul className="space-y-2 text-sm">{occurrences.data?.map(item => <li key={item.id}>{new Date(item.scheduledAt).toLocaleString('tr-TR')} · {item.outcome} · {item.outcome === 'AdmissionDenied' ? failureMessage(item.reason) : item.reason}{item.runId && <> · <Link className="text-primary underline" to={`/tenants/${tenantId}/backup-runs/${item.runId}`}>Çalıştırmayı aç</Link></>}</li>)}</ul></QueryState></Section>}
      {cannotSave && <Notice tone="warning" title="Depolama hedefi kaydedilemiyor" action={<Button type="button" variant="outline" size="sm" aria-label="Adıma git: Depolama (hedef sorunu)" onClick={() => go(2)}>Adıma git</Button>}>Hedef depolama bağlantısı doğrulanamadı veya kullanılamıyor. Depolama adımında hedefi düzeltin.</Notice>}
    </>}
    {save.error && <Notice tone="error" title="İş kaydedilemedi">
      <p>{errorMessage(save.error)}</p>
      {errorSteps.length > 0 && <div className="mt-3 flex flex-wrap gap-2">{errorSteps.map(item => <Button key={item} type="button" variant="outline" size="sm" aria-label={`Hatalı alana git: ${jobSteps[item]}`} onClick={() => go(item)}>Adıma git</Button>)}</div>}
    </Notice>}
    <div className="flex flex-wrap items-center justify-between gap-3">
      <Button type="button" variant="outline" disabled={step === 0 || save.isPending} onClick={() => go(step - 1)}>Geri</Button>
      <div className="flex flex-wrap items-center gap-3">
        <Link className="text-sm text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>Vazgeç</Link>
        {job && !last && <Button type="button" variant="outline" onClick={() => advance(summaryStep)}>Özete dön</Button>}
        {last ? <Button key="save" type="submit" size="lg" disabled={save.isPending || cannotSave}>{save.isPending ? 'Kaydediliyor…' : 'Kaydet'}</Button> : <Button key="next" type="submit">İleri</Button>}
      </div>
    </div>
  </form>
}
