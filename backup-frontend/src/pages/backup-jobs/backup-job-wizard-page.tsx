import { useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { QueryState } from '@/components/data/query-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getAgents } from '@/features/agents/api'
import { useBackupJob, useJobOccurrences, useSaveBackupJob } from '@/features/backup-jobs/hooks'
import type { BackupJob, BackupJobInput, BackupConfiguration, Schedule, RetryPolicy, RetentionPolicy } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'

const initialConfiguration: BackupConfiguration = { engine: 'mssql', database: '', host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storageProvider: 'local', storagePrefix: '', storageExecutionLocation: 'Agent', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred' }
const initialSchedule: Schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 3600, overlapPolicy: 'Skip' }
const initialRetry: RetryPolicy = { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 }
const initialRetention: RetentionPolicy = { enabled: true, keepForSeconds: 1209600, deleteGracePeriodSeconds: 86400 }
const inputClass = 'h-9 w-full rounded-md border border-border bg-surface-2 px-3 text-sm outline-none focus:border-ring disabled:opacity-60'

function Field({ name, label, errors, children }: { name: string; label: string; errors: Record<string, string>; children: ReactNode }) {
  const message = errors[name] ?? errors[name.split('.').at(-1) ?? name]
  return <div className="space-y-1"><label htmlFor={name} className="block text-sm font-medium text-text-heading">{label}</label>{children}{message && <p id={`${name}-error`} role="alert" className="text-sm text-destructive">{message}</p>}</div>
}
function Section({ title, children }: { title: string; children: ReactNode }) {
  return <section className="space-y-4 rounded-lg border border-border bg-surface p-4 sm:p-6"><h2 className="text-lg font-medium text-text-heading">{title}</h2><div className="grid gap-4 md:grid-cols-2">{children}</div></section>
}
export function BackupJobWizardPage() {
  const { id = '' } = useParams()
  const { canOperate } = useTenant()
  const job = useBackupJob(id)
  if (!canOperate) return <p role="alert">Bu sayfada değişiklik yapma yetkiniz yok. Viewer rolü salt okunurdur.</p>
  if (id) return <QueryState isPending={job.isPending} error={job.error}>{job.data && <JobForm key={id} job={job.data} />}</QueryState>
  return <JobForm />
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
  const saving = useRef(false)
  const errors: Record<string, string> = save.error instanceof ApiError ? { ...Object.fromEntries(Object.entries(save.error.fields).map(([field, code]) => [field, failureMessage(code)])), ...(save.error.field ? { [save.error.field]: errorMessage(save.error) } : {}) } : {}
  function text(name: string, label: string, value: string, onChange: (value: string) => void, disabled = false) {
    return <Field name={name} label={label} errors={errors}><input id={name} className={inputClass} value={value} disabled={disabled} aria-invalid={Boolean(errors[name])} aria-describedby={`${name}-error`} onChange={event => onChange(event.target.value)} /></Field>
  }
  function number(name: string, label: string, value: number | null, onChange: (value: number | null) => void, disabled = false) {
    return <Field name={name} label={label} errors={errors}><input id={name} type="number" step="any" className={inputClass} value={value ?? ''} disabled={disabled} aria-invalid={Boolean(errors[name])} aria-describedby={`${name}-error`} onChange={event => onChange(event.target.value === '' ? null : Number(event.target.value))} /></Field>
  }
  function check(label: string, value: boolean, onChange: (value: boolean) => void, disabled = false) {
    return <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={value} disabled={disabled} onChange={event => onChange(event.target.checked)} />{label}</label>
  }
  function selection(name: string, label: string, value: string, options: readonly (readonly [string, string])[], onChange: (value: string) => void) {
    return <Field name={name} label={label} errors={errors}><select id={name} className={inputClass} value={value} aria-invalid={Boolean(errors[name])} aria-describedby={`${name}-error`} onChange={event => onChange(event.target.value)}>{options.map(([key, text]) => <option key={key} value={key}>{text}</option>)}</select></Field>
  }
  const policies = [['Disabled', 'Off — kapalı'], ['Preferred', 'IfSupported — destekleniyorsa'], ['Required', 'Required — zorunlu']] as const
  return <form noValidate className="space-y-5" onSubmit={event => {
    event.preventDefault()
    if (saving.current) return
    saving.current = true
    const input: BackupJobInput = { name, agentId, configuration, retryPolicy: retry, overlapPolicy: overlap, enabled, schedule: scheduled ? { ...schedule, overlapPolicy: overlap } : null, ...(canAdmin ? { retention } : {}) }
    save.mutate(input, { onSuccess: () => navigate(`/tenants/${tenantId}/backup-jobs`), onSettled: () => { saving.current = false } })
  }}>
    <Link className="text-sm text-primary hover:underline" to={`/tenants/${tenantId}/backup-jobs`}>← Yedekleme işlerine dön</Link>
    <h1 className="text-2xl font-semibold text-text-heading">{job ? 'Yedekleme işini düzenle' : 'Yeni yedekleme işi'}</h1>
    <Section title="İş ve Agent">
      {text('name', 'İş adı', name, setName)}
      <Field name="agentId" label="Agent" errors={errors}><select id="agentId" className={inputClass} value={agentId} onChange={event => setAgentId(event.target.value)}><option value="">Agent seçin</option>{agents.data?.map(agent => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? 'Adsız Agent'} · {agent.operationalStatus}</option>)}</select></Field>
      {agents.isPending && <p role="status" className="text-sm">Agent listesi yükleniyor…</p>}{agents.error && <p role="alert" className="text-sm text-destructive">{errorMessage(agents.error)}</p>}
      {check('İş etkin', enabled, setEnabled)}
    </Section>
    <Section title="MSSQL kaynak ve bağlantı">
      {text('configuration.host', 'SQL Server host / instance', configuration.host, host => setConfiguration({ ...configuration, host }))}
      {text('configuration.database', 'Database', configuration.database, database => setConfiguration({ ...configuration, database }))}
      {number('configuration.port', 'Port (isteğe bağlı)', configuration.port, port => setConfiguration({ ...configuration, port }))}
      {text('configuration.databaseCredentialReference', 'Kimlik bilgisi referansı (parola değil)', configuration.databaseCredentialReference ?? '', databaseCredentialReference => setConfiguration({ ...configuration, databaseCredentialReference: databaseCredentialReference || null }), configuration.integratedSecurity)}
      {check('Windows / integrated security', configuration.integratedSecurity, integratedSecurity => setConfiguration({ ...configuration, integratedSecurity }))}
      {check('Bağlantıyı şifrele', configuration.encrypt, encrypt => setConfiguration({ ...configuration, encrypt }))}
      {check('Sunucu sertifikasına güven', configuration.trustServerCertificate, trustServerCertificate => setConfiguration({ ...configuration, trustServerCertificate }))}
      <p className="text-sm text-text-muted md:col-span-2">Bu form parola kabul etmez. Kimlik bilgileri Agent yapılandırmasında tutulur; yalnızca referans adı gönderilir.</p>
    </Section>
    <Section title="Yedekleme seçenekleri">
      {check('COPY_ONLY', configuration.copyOnly, copyOnly => setConfiguration({ ...configuration, copyOnly }))}
      {selection('configuration.checksumPolicy', 'CHECKSUM', configuration.checksumPolicy, policies, value => setConfiguration({ ...configuration, checksumPolicy: value as BackupConfiguration['checksumPolicy'] }))}
      {selection('configuration.compressionPolicy', 'Compression', configuration.compressionPolicy, policies, value => setConfiguration({ ...configuration, compressionPolicy: value as BackupConfiguration['compressionPolicy'] }))}
    </Section>
    <Section title="Depolama ve yürütme">
      {text('configuration.storageProvider', 'Storage Provider referansı', configuration.storageProvider, storageProvider => setConfiguration({ ...configuration, storageProvider }))}
      {text('configuration.storagePrefix', 'Hedef öneki (göreli anahtar)', configuration.storagePrefix, storagePrefix => setConfiguration({ ...configuration, storagePrefix }))}
      {selection('configuration.storageExecutionLocation', 'Depolama yürütme konumu', configuration.storageExecutionLocation, [['Agent', 'Agent'], ['Backend', 'Backend']], value => setConfiguration({ ...configuration, storageExecutionLocation: value as BackupConfiguration['storageExecutionLocation'] }))}
      {text('configuration.stagingReference', 'Staging referansı', configuration.stagingReference, stagingReference => setConfiguration({ ...configuration, stagingReference }))}
    </Section>
    <Section title="Zamanlama">
      {check('Zamanlama kullan', scheduled, setScheduled)}
      {scheduled && <>{check('Zamanlama etkin', schedule.enabled, value => setSchedule({ ...schedule, enabled: value }))}
      {text('cronExpression', 'Cron ifadesi', schedule.cronExpression, cronExpression => setSchedule({ ...schedule, cronExpression }))}
      {text('timeZone', 'Saat dilimi (IANA / sistem adı)', schedule.timeZone, timeZone => setSchedule({ ...schedule, timeZone }))}
      {number('missedRunGraceSeconds', 'Kaçırılan çalışma toleransı (saniye)', schedule.missedRunGraceSeconds, value => setSchedule({ ...schedule, missedRunGraceSeconds: value ?? 0 }))}</>}
      {selection('overlapPolicy', 'Çakışma politikası', overlap, [['Skip', 'Skip — etkin çalışma varsa atla'], ['Queue', 'Queue — kuyruğa al']], value => setOverlap(value as 'Skip' | 'Queue'))}
      {job && <p className="text-sm text-text-muted md:col-span-2">Kaydedilmiş zamanlamanın sonraki çalışması: {job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt ? new Date((job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt)!).toLocaleString('tr-TR') : 'Planlanmamış'}. Değişiklikler kaydedildikten sonra sunucu tarafından hesaplanır.</p>}
    </Section>
    <Section title="Yeniden deneme">
      {number('retryPolicy.maxAttempts', 'Toplam deneme sayısı', retry.maxAttempts, value => setRetry({ ...retry, maxAttempts: value ?? 0 }))}
      {number('retryPolicy.initialDelaySeconds', 'İlk gecikme (saniye)', retry.initialDelaySeconds, value => setRetry({ ...retry, initialDelaySeconds: value ?? 0 }))}
      {number('retryPolicy.maxDelaySeconds', 'En uzun gecikme (saniye)', retry.maxDelaySeconds, value => setRetry({ ...retry, maxDelaySeconds: value ?? 0 }))}
      {selection('retryPolicy.backoff', 'Gecikme stratejisi', retry.backoff, [['Fixed', 'Fixed — sabit'], ['Exponential', 'Exponential — üstel']], backoff => setRetry({ ...retry, backoff }))}
      {number('retryPolicy.jitter', 'Jitter oranı', retry.jitter, value => setRetry({ ...retry, jitter: value ?? 0 }))}
    </Section>
    <Section title="Saklama politikası">
      {!canAdmin && <p className="text-sm text-text-muted md:col-span-2">Saklama politikasını yalnızca Owner / Admin değiştirebilir. {job ? 'Mevcut politika korunur.' : 'Sunucunun varsayılan politikası uygulanır.'}</p>}
      {check('Saklama süresi etkin', retention.enabled, value => setRetention({ ...retention, enabled: value }), !canAdmin)}
      {number('retention.keepForSeconds', 'KeepFor — saklama süresi (saniye)', retention.keepForSeconds, value => setRetention({ ...retention, keepForSeconds: value ?? 0 }), !canAdmin)}
      {number('retention.deleteGracePeriodSeconds', 'DeleteGracePeriod — silme bekleme süresi (saniye)', retention.deleteGracePeriodSeconds, value => setRetention({ ...retention, deleteGracePeriodSeconds: value ?? 0 }), !canAdmin)}
      <p className="text-sm text-text-muted md:col-span-2">Politika değişikliği mevcut yedeklerin son kullanma tarihini değiştirmez.</p>
    </Section>
    {job && <section className="space-y-3 rounded-lg border border-border bg-surface p-4"><h2 className="text-lg font-medium">Son zamanlama oluşumları</h2><QueryState isPending={occurrences.isPending} error={occurrences.error} empty={occurrences.data?.length === 0}><ul className="space-y-2 text-sm">{occurrences.data?.map(item => <li key={item.id}>{new Date(item.scheduledAt).toLocaleString('tr-TR')} · {item.outcome} · {item.reason}{item.runId && <> · <Link className="text-primary underline" to={`/tenants/${tenantId}/backup-runs/${item.runId}`}>Çalıştırmayı aç</Link></>}</li>)}</ul></QueryState></section>}
    {save.error && <div role="alert" className="rounded-md border border-destructive/30 p-3 text-sm">{errorMessage(save.error)}</div>}
    <div className="flex justify-end"><Button type="submit" disabled={save.isPending}>{save.isPending ? 'Kaydediliyor…' : 'Kaydet'}</Button></div>
  </form>
}
