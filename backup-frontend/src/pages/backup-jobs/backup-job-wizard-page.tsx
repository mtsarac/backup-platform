import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getAgents } from '@/features/agents/api'
import { getDiscoveryInstance } from '@/features/discovery/api'
import { useDiscoveryInstance } from '@/features/discovery/hooks'
import { discoveredConfiguration, selectableNames } from '@/features/discovery/job-prefill'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { useBackupJob, useJobOccurrences, useSaveBackupJob } from '@/features/backup-jobs/hooks'
import { Advanced, FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit } from '@/features/backup-jobs/job-field-kit'
import { JobSummary } from '@/features/backup-jobs/job-summary'
import { BackupHealthCard } from '@/features/backup-jobs/health-card'
import { jobSteps, schedulePresets, stepOfField, summaryStep, validateStep } from '@/features/backup-jobs/job-wizard-model'
import { SourceStep } from '@/features/backup-jobs/source-step'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { connectionUnavailableReason, primaryDestination, resolveDestinationConnection } from '@/features/storage-connections/job-connection-picker-utils'
import { MultiDestinationPicker } from '@/features/storage-connections/multi-destination-picker'
import type { BackupJob, BackupJobInput, BackupConfiguration, Schedule, RetryPolicy, RetentionPolicy } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { encryptionPolicies, type EncryptionPolicy } from '@/features/artifact-encryption/types'
import { policyDescriptions, policyLabels } from '@/features/artifact-encryption/labels'

const initialConfiguration: BackupConfiguration = { engine: 'mssql', database: '', host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }], requiredVerifiedReplicaCount: 1, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off' }
const initialSchedule: Schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 3600, overlapPolicy: 'Skip' }
const initialRetry: RetryPolicy = { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 }
const initialRetention: RetentionPolicy = { enabled: true, keepForSeconds: 1209600, deleteGracePeriodSeconds: 86400 }
const policies = [['Disabled', 'Off — kapalı'], ['Preferred', 'IfSupported — destekleniyorsa'], ['Required', 'Required — zorunlu']] as const

export function BackupJobWizardPage() {
  useLanguage()
  const { id = '' } = useParams()
  const { tenantId, canOperate } = useTenant()
  const job = useBackupJob(id)
  const [params] = useSearchParams()
  const discoveryInstance = id ? '' : params.get('discoveryInstance') ?? ''
  const discovered = useDiscoveryInstance(discoveryInstance)
  const prefill = discovered.data ? { instance: discovered.data, databases: selectableNames(discovered.data, params.getAll('database')) } : undefined
  const readOnly = Boolean(id) && !canOperate
  const crumbs = [{ label: tx('Yedekleme işleri'), to: `/tenants/${tenantId}/backup-jobs` }, { label: id ? tx(readOnly ? 'Yedekleme sağlığı' : 'Düzenle') : tx('Yeni iş') }]
  let body
  if (!canOperate) body = readOnly ? <p className="text-sm text-text-muted">{tx('Salt okunur')}</p> : <Notice tone="error">{tx("Bu sayfada değişiklik yapma yetkiniz yok. Viewer rolü salt okunurdur.")}</Notice>
  else if (id) body = <QueryState isPending={job.isPending} error={job.error}>{job.data && <JobForm key={`${tenantId}:${id}`} job={job.data} />}</QueryState>
  else if (discoveryInstance && discovered.isPending) body = <QueryState isPending error={null} />
  else body = <>
    {discovered.error && <Notice tone="warning" title={tx("Keşfedilen veritabanları yüklenemedi")}>{errorMessage(discovered.error)}{' '}{tx("Kaynağı elle girebilirsiniz.")}</Notice>}
    <JobForm key={`${tenantId}:new`} prefill={prefill} />
  </>
  return <div className="space-y-6">
    <PageHeader crumbs={crumbs} title={readOnly ? tx('Yedekleme sağlığı') : id ? tx('Yedekleme işini düzenle') : tx('Yeni yedekleme işi')} description={readOnly ? job.data?.name : tx("Kaynak, depolama, zamanlama ve saklama ayarlarını adım adım belirleyin.")} />
    {id && <BackupHealthCard key={`${tenantId}:${id}`} jobId={id} />}
    {body}
  </div>
}

function JobForm({ job, prefill }: { job?: BackupJob; prefill?: { instance: DiscoveryInstanceDetail; databases: string[] } }) {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const navigate = useNavigate()
  const save = useSaveBackupJob(job?.id)
  const occurrences = useJobOccurrences(job?.id ?? '')
  const agents = useQuery({ queryKey: ['tenant', tenantId, 'agents'], queryFn: ({ signal }) => getAgents(tenantId, signal) })
  const [name, setName] = useState(job?.name ?? '')
  const [agentId, setAgentId] = useState(job?.agentId ?? prefill?.instance.agentId ?? '')
  const [configuration, setConfiguration] = useState(() => job?.configuration.engine === 'postgres' ? { ...job.configuration, copyOnly: false } : job?.configuration ?? (prefill?.databases.length ? discoveredConfiguration(initialConfiguration, prefill.instance, prefill.databases) : initialConfiguration))
  // Databases picked from the discovery inventory; one job is created per name when the user confirms the summary.
  const [selected, setSelected] = useState<string[]>(prefill?.databases ?? [])
  const [results, setResults] = useState<Record<string, string>>({})
  const [discoveryInstanceId, setDiscoveryInstanceId] = useState(prefill?.instance.id ?? '')
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
  const multi = selected.length > 1
  const saveError = multi ? null : save.error
  const serverErrors: Record<string, string> = saveError instanceof ApiError ? { ...Object.fromEntries(Object.entries(saveError.fields).map(([field, code]) => [field.replace(/^schedule\./, ''), failureMessage(code)])), ...(saveError.field ? { [saveError.field.replace(/^schedule\./, '')]: errorMessage(saveError) } : {}) } : {}
  const errors = { ...serverErrors, ...clientErrors }
  const errorSteps = [...new Set(Object.keys(serverErrors).map(stepOfField).filter((value): value is number => value !== undefined))].sort((a, b) => a - b)
  const { text, number, check, select } = fieldKit(errors)
  const withErrors = (...names: string[]) => names.some(item => errors[item])
  const nextOccurrence = job && (job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt)
  const nextRun = job ? nextOccurrence ? new Date(nextOccurrence).toLocaleString(currentLocale()) : tx('Planlanmamış') : undefined
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
    if (multi) { void createMany(input); return }
    save.mutate(input, {
      onSuccess: () => navigate(`/tenants/${tenantId}/backup-jobs`),
      onError: error => {
        if (error instanceof ApiError && ['storage_connection_disabled', 'storage_connection_unavailable'].includes(error.reason ?? error.code ?? '')) refreshConnections()
      },
      onSettled: () => { saving.current = false },
    })
  }
  async function createMany(input: BackupJobInput) {
    const outcome = { ...results }
    // A create that failed on the client side (timeout) may still have succeeded on the server: before retrying, look at the inventory's jobs.
    const covered = await coveredAfterFailure(selected.filter(item => outcome[item] && outcome[item] !== 'ok'))
    for (const database of selected.filter(item => outcome[item] !== 'ok')) {
      if (covered.has(database)) { outcome[database] = 'ok'; continue }
      try {
        await save.mutateAsync({ ...input, name: `${input.name} – ${database}`, configuration: { ...input.configuration, database } })
        outcome[database] = 'ok'
      } catch (error) { outcome[database] = errorMessage(error) }
      setResults({ ...outcome })
    }
    setResults({ ...outcome })
    saving.current = false
    if (selected.every(item => outcome[item] === 'ok')) navigate(`/tenants/${tenantId}/backup-jobs`)
  }
  async function coveredAfterFailure(retrying: string[]) {
    if (!discoveryInstanceId || retrying.length === 0) return new Set<string>()
    try {
      const detail = await getDiscoveryInstance(tenantId, discoveryInstanceId)
      return new Set(detail.databases.filter(database => retrying.includes(database.name) && database.jobs.length > 0).map(database => database.name))
    } catch { return new Set<string>() }
  }
  function applyDiscovery(instance: DiscoveryInstanceDetail, databases: string[]) {
    setAgentId(instance.agentId)
    setDiscoveryInstanceId(instance.id)
    setConfiguration(discoveredConfiguration(configuration, instance, databases))
    setSelected(databases)
    setResults({})
  }
  function clearDiscovery() {
    setSelected([])
    setResults({})
    setConfiguration({ ...configuration, database: '' })
  }
  const failedCount = Object.values(results).filter(value => value !== 'ok').length
  const last = step === summaryStep
  return <form ref={formRef} noValidate className="space-y-5" onSubmit={event => {
    event.preventDefault()
    if (last) submit()
    else advance(step + 1)
  }}>
    <Stepper steps={jobSteps.map(step => tx(step))} current={step} />
    <h2 ref={headingRef} tabIndex={-1} className="sr-only">{tx("Adım")}{' '}{step + 1} / {tx(jobSteps.length)}: {tx(jobSteps[step])}</h2>
    {step === 0 && <Section title={tx("İş ve Agent")} description={tx("İşin adı ve yedeği alacak Agent.")}>
      <FormGrid>
        {text('name', tx('İş adı'), name, setName)}
        <div className="min-w-0 space-y-1">
          {select('agentId', tx('Agent'), agentId, [['', tx('Agent seçin')], ...(agents.data ?? []).map(item => [item.agentId, `${item.hostName ?? tx('Adsız Agent')} · ${tx(item.operationalStatus)}`] as const)], setAgentId)}
          {agents.isPending && <p role="status" className="text-sm text-text-muted">{tx("Agent listesi yükleniyor…")}</p>}{agents.error && <p role="alert" className="text-sm text-error">{errorMessage(agents.error)}</p>}
        </div>
        {check(tx('İş etkin'), enabled, setEnabled)}
      </FormGrid>
    </Section>}
    {step === 1 && <SourceStep key={configuration.engine} configuration={configuration} errors={errors} discovery={job ? undefined : { agents: agents.data ?? [], agentId, selected, onApply: applyDiscovery, onClear: clearDiscovery }}
      onChange={next => { if (next.engine !== configuration.engine) { setSelected([]); setResults({}) } setConfiguration(next) }} />}
    {step === 2 && <>
      <Section title={tx("Depolama ve yürütme")} description={tx("Yedeğin yazılacağı hedefler.")}>
        <MultiDestinationPicker configuration={configuration} connections={connectionItems} providers={providerItems} pending={pickerPending} error={pickerError} fieldError={Object.entries(errors).find(([field]) => field.startsWith('configuration.destinations'))?.[1] ?? errors['configuration.requiredVerifiedReplicaCount']} existingTarget={existingTarget} onChange={setConfiguration} onRefresh={refreshConnections} />
      </Section>
      <Advanced title={tx("Depolama ayrıntıları")} open={withErrors('configuration.storagePrefix', 'configuration.stagingReference')}>
        {text('configuration.storagePrefix', tx('Hedef öneki (göreli anahtar)'), configuration.storagePrefix, storagePrefix => setConfiguration({ ...configuration, storagePrefix }))}
        {text('configuration.stagingReference', tx('Geçici alan referansı (staging)'), configuration.stagingReference, stagingReference => setConfiguration({ ...configuration, stagingReference }))}
      </Advanced>
    </>}
    {step === 3 && <>
      <Section title={tx("Zamanlama")} description={tx("Manuel çalıştırma her zaman mümkündür; zamanlama isteğe bağlıdır.")}>
        <FormGrid>
          {check(tx('Zamanlama kullan'), scheduled, setScheduled)}
          {scheduled && check(tx('Zamanlama etkin'), schedule.enabled, value => setSchedule({ ...schedule, enabled: value }))}
          {scheduled && <div role="group" aria-label={tx("Hazır zamanlamalar")} className="flex flex-wrap gap-2 md:col-span-2">{schedulePresets.map(([label, cron]) => <Button key={cron} type="button" variant={schedule.cronExpression === cron ? 'default' : 'outline'} aria-pressed={schedule.cronExpression === cron} onClick={() => setSchedule({ ...schedule, cronExpression: cron })}>{tx(label)}</Button>)}</div>}
          {nextRun && <p className="text-sm text-text-muted md:col-span-2">{tx("Kaydedilmiş zamanlamanın sonraki çalışması:")}{' '}{nextRun}{tx(". Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.")}</p>}
        </FormGrid>
      </Section>
      <Advanced title={tx("Gelişmiş zamanlama")} open={withErrors('cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy')}>
        {scheduled && text('cronExpression', tx('Cron ifadesi'), schedule.cronExpression, cronExpression => setSchedule({ ...schedule, cronExpression }))}
        {scheduled && text('timeZone', tx('Saat dilimi (IANA / sistem adı)'), schedule.timeZone, timeZone => setSchedule({ ...schedule, timeZone }))}
        {scheduled && number('missedRunGraceSeconds', tx('Kaçırılan çalışma toleransı (saniye)'), schedule.missedRunGraceSeconds, value => setSchedule({ ...schedule, missedRunGraceSeconds: value ?? 0 }))}
        {select('overlapPolicy', tx('Önceki çalışma sürerken yeni zamanlama'), overlap, [['Skip', tx('Atla')], ['Queue', tx('Kuyruğa al')]], value => setOverlap(value as 'Skip' | 'Queue'))}
      </Advanced>
      <Advanced title={tx("Yeniden deneme")} open={withErrors('retryPolicy.maxAttempts', 'retryPolicy.initialDelaySeconds', 'retryPolicy.maxDelaySeconds', 'retryPolicy.backoff', 'retryPolicy.jitter')}>
        {number('retryPolicy.maxAttempts', tx('Toplam deneme sayısı'), retry.maxAttempts, value => setRetry({ ...retry, maxAttempts: value ?? 0 }))}
        {number('retryPolicy.initialDelaySeconds', tx('İlk gecikme (saniye)'), retry.initialDelaySeconds, value => setRetry({ ...retry, initialDelaySeconds: value ?? 0 }))}
        {number('retryPolicy.maxDelaySeconds', tx('En uzun gecikme (saniye)'), retry.maxDelaySeconds, value => setRetry({ ...retry, maxDelaySeconds: value ?? 0 }))}
        {select('retryPolicy.backoff', tx('Gecikme stratejisi'), retry.backoff, [['Fixed', tx('Fixed — sabit')], ['Exponential', tx('Exponential — üstel')]], backoff => setRetry({ ...retry, backoff }))}
        {number('retryPolicy.jitter', tx('Rastgele gecikme oranı (jitter)'), retry.jitter, value => setRetry({ ...retry, jitter: value ?? 0 }))}
      </Advanced>
    </>}
    {step === 4 && <>
      <Section title={tx("Saklama politikası")} description={tx("Yedeklerin ne kadar süre saklanacağı.")}>
        <FormGrid>
          {!canAdmin && <p className="text-sm text-text-muted md:col-span-2">{tx("Saklama politikasını yalnızca Owner / Admin değiştirebilir.")}{' '}{job ? tx('Mevcut politika korunur.') : tx('Sunucunun varsayılan politikası uygulanır.')}</p>}
          {check(tx('Saklama süresi etkin'), retention.enabled, value => setRetention({ ...retention, enabled: value }), !canAdmin)}
          {number('retention.keepForSeconds', tx('KeepFor — saklama süresi (saniye)'), retention.keepForSeconds, value => setRetention({ ...retention, keepForSeconds: value ?? 0 }), !canAdmin)}
          {number('retention.deleteGracePeriodSeconds', tx('DeleteGracePeriod — silme bekleme süresi (saniye)'), retention.deleteGracePeriodSeconds, value => setRetention({ ...retention, deleteGracePeriodSeconds: value ?? 0 }), !canAdmin)}
          <p className="text-sm text-text-muted md:col-span-2">{tx("Politika değişikliği mevcut yedeklerin son kullanma tarihini değiştirmez.")}</p>
        </FormGrid>
      </Section>
      <Section title={tx("Yedekleme seçenekleri")}>
        <FormGrid>
          {configuration.engine !== 'postgres' && check(tx('Kopya yedek (COPY_ONLY: mevcut yedek zincirini etkilemez)'), configuration.copyOnly, copyOnly => setConfiguration({ ...configuration, copyOnly }))}
          {select('configuration.checksumPolicy', configuration.engine === 'postgres' ? tx('Sayfa bütünlüğü (data_checksums)') : tx('Bütünlük doğrulaması (CHECKSUM)'), configuration.checksumPolicy, policies, value => setConfiguration({ ...configuration, checksumPolicy: value as BackupConfiguration['checksumPolicy'] }))}
          {select('configuration.compressionPolicy', tx('Sıkıştırma (Compression)'), configuration.compressionPolicy, policies, value => setConfiguration({ ...configuration, compressionPolicy: value as BackupConfiguration['compressionPolicy'] }))}
          <div className="min-w-0 space-y-1 md:col-span-2">
            {select('configuration.encryptionPolicy', tx('Yedek şifreleme politikası'), configuration.encryptionPolicy, encryptionPolicies.map(policy => [policy, policyLabels[policy]] as const), value => setConfiguration({ ...configuration, encryptionPolicy: value as EncryptionPolicy }))}
            <p className="text-sm text-text-muted">{policyDescriptions[configuration.encryptionPolicy]}{' '}{tx("Şifreleme Agent'ta, depolamaya yazılmadan önce yapılır; yalnızca sonraki çalıştırmalar etkilenir, mevcut yedekler değişmez.")}</p>
          </div>
          {configuration.engine === 'postgres' && <p className="text-sm text-text-muted md:col-span-2">{tx("pg_dump custom arşivi mevcut yedek zincirini değiştirmez; COPY_ONLY uygulanmaz. data_checksums sunucunun sayfa doğrulamasıdır: Required yalnızca etkin kümelerde çalışır, Preferred desteklenmiyorsa uyarır. Arşiv ve replikalar her durumda SHA-256 ile doğrulanır. Sıkıştırma pg_dump arşiv sıkıştırmasıdır.")}</p>}
        </FormGrid>
      </Section>
    </>}
    {last && <>
      <JobSummary name={name} agentLabel={agent ? `${agent.hostName ?? tx('Adsız Agent')} · ${agent.operationalStatus}` : agentId ? tx('Agent bulunamadı') : tx('Seçilmedi')} enabled={enabled} configuration={configuration} scheduled={scheduled} schedule={schedule} overlap={overlap} retry={retry} retention={retention} canAdmin={canAdmin} connections={connectionItems} nextRun={nextRun} onGo={go} />
      {multi && <Section title={tx("Oluşturulacak işler")} description={tx("{0} veritabanı için ayrı Backup Job oluşturulur. Hiçbiri 'Kaydet' düğmesine basılana kadar oluşturulmaz.", { "0": selected.length })}>
        <ul className="space-y-1 text-sm">{selected.map(database => <li key={database}>{name} – {database}{results[database] === 'ok' && <span className="text-success">{' '}{tx("· Oluşturuldu")}</span>}{results[database] && results[database] !== 'ok' && <span role="alert" className="text-error">{' '}{tx("· Oluşturulamadı:")}{' '}{results[database]}</span>}</li>)}</ul>
        {failedCount > 0 && <p className="mt-3 text-sm text-text-muted">{tx("Başarısız olanlar yeniden denenebilir; oluşturulanlar tekrar gönderilmez.")}</p>}
      </Section>}
      {job && <Section title={tx("Son zamanlama oluşumları")}><QueryState isPending={occurrences.isPending} error={occurrences.error} empty={occurrences.data?.length === 0}><ul className="space-y-2 text-sm">{occurrences.data?.map(item => <li key={item.id}>{new Date(item.scheduledAt).toLocaleString(currentLocale())} · {tx(item.outcome)} · {item.outcome === 'AdmissionDenied' ? failureMessage(item.reason) : item.reason}{item.runId && <> · <Link className="text-primary underline" to={`/tenants/${tenantId}/backup-runs/${item.runId}`}>{tx("Çalıştırmayı aç")}</Link></>}</li>)}</ul></QueryState></Section>}
      {cannotSave && <Notice tone="warning" title={tx("Depolama hedefi kaydedilemiyor")} action={<Button type="button" variant="outline" size="sm" aria-label={tx("Adıma git: Depolama (hedef sorunu)")} onClick={() => go(2)}>{tx("Adıma git")}</Button>}>{tx("Hedef depolama bağlantısı doğrulanamadı veya kullanılamıyor. Depolama adımında hedefi düzeltin.")}</Notice>}
    </>}
    {saveError && <Notice tone="error" title={tx("İş kaydedilemedi")}>
      <p>{errorMessage(saveError)}</p>
      {errorSteps.length > 0 && <div className="mt-3 flex flex-wrap gap-2">{errorSteps.map(item => <Button key={item} type="button" variant="outline" size="sm" aria-label={tx("Hatalı alana git: {0}", { "0": jobSteps[item] })} onClick={() => go(item)}>{tx("Adıma git")}</Button>)}</div>}
    </Notice>}
    <div className="flex flex-wrap items-center justify-between gap-3">
      <Button type="button" variant="outline" disabled={step === 0 || save.isPending} onClick={() => go(step - 1)}>{tx("Geri")}</Button>
      <div className="flex flex-wrap items-center gap-3">
        <Link className="text-sm text-primary underline-offset-4 hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>{tx("Vazgeç")}</Link>
        {job && !last && <Button type="button" variant="outline" onClick={() => advance(summaryStep)}>{tx("Özete dön")}</Button>}
        {last ? <Button key="save" type="submit" size="lg" disabled={save.isPending || cannotSave}>{save.isPending ? tx('Kaydediliyor…') : failedCount > 0 ? tx('Başarısızları yeniden dene') : tx('Kaydet')}</Button> : <Button key="next" type="submit">{tx("İleri")}</Button>}
      </div>
    </div>
  </form>
}
