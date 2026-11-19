import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AccessDenied } from '@/components/auth/access-denied'
import { QueryState } from '@/components/data/query-state'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { Stepper } from '@/components/ui/stepper'
import { useAgents } from '@/features/agents/hooks'
import { useRunBackupJob, useSaveBackupJob } from '@/features/backup-jobs/hooks'
import { ComputerStep } from '@/features/first-backup/computer-step'
import { DataStep } from '@/features/first-backup/data-step'
import { autoJobName, awaitingCapabilities, CAPABILITY_POLL_MS, defaultSettings, firstBackupJobInput, type FirstBackupItem, type FirstBackupSettings } from '@/features/first-backup/first-backup-model'
import { ProgressStep, ReadyStep } from '@/features/first-backup/progress-step'
import { SummaryStep, type ItemResult } from '@/features/first-backup/summary-step'
import { useTenant } from '@/features/tenancy/use-tenant'

type Step = 'computer' | 'data' | 'summary'
/** `''` until jobs exist; from then on the earlier steps are closed. `ready`: created only, first backup waits for its schedule. */
type Phase = '' | 'ready' | 'running'

/** `…/first-backup`: add a computer when there is none, then choose what to back up, review, and create one job per pick (optionally starting its first backup). */
export function FirstBackupPage() {
  useLanguage()
  const { tenantId, canOperate, canAdmin } = useTenant()
  const base = `/tenants/${tenantId}`
  const agents = useAgents(tenantId, false, list => awaitingCapabilities(list) ? CAPABILITY_POLL_MS : false)
  const save = useSaveBackupJob()
  const { run } = useRunBackupJob()
  const [step, setStep] = useState<Step | null>(null)
  const [picked, setPicked] = useState<ReadonlySet<string> | null>(null)
  const [names, setNames] = useState<Record<string, string>>({})
  const [items, setItems] = useState<FirstBackupItem[]>([])
  const [settings, setSettings] = useState<FirstBackupSettings>(defaultSettings)
  const [results, setResults] = useState<Record<string, ItemResult>>({})
  const [pending, setPending] = useState(false)
  const [phase, setPhase] = useState<Phase>('')
  // Decided once from the first fresh list (not a stale cached one): the list changing later (a computer enrolling) must not move the user to another screen.
  if (step === null && agents.data && !agents.isFetching) setStep(agents.data.length ? 'data' : 'computer')

  const itemName = (item: FirstBackupItem) => names[item.key]?.trim() || autoJobName(item.database)
  async function computerOnline() {
    await agents.refetch()
    setStep('data')
  }
  async function create(startFirstBackup: boolean) {
    setPending(true)
    const outcome = { ...results }
    for (const item of items) {
      const previous = outcome[item.key] ?? {}
      let next: ItemResult = { jobId: previous.jobId, runId: previous.runId }
      try {
        if (!next.jobId) next.jobId = (await save.mutateAsync(firstBackupJobInput(item, itemName(item), settings, canAdmin))).id
        if (startFirstBackup && !next.runId) next.runId = (await run(next.jobId))?.id
      } catch (error) { next = { ...next, error } }
      outcome[item.key] = next
      setResults({ ...outcome })
    }
    setPending(false)
    if (items.every(item => outcome[item.key].jobId && !outcome[item.key].error)) setPhase(startFirstBackup ? 'running' : 'ready')
  }

  let body
  if (!canOperate) body = <AccessDenied what={tx('Bu sayfada değişiklik yapma yetkiniz yok. Viewer rolü salt okunurdur.')} />
  else body = <QueryState isPending={agents.isPending || step === null} error={agents.error}>
    {!phase && step === 'computer' && (canAdmin
      ? <ComputerStep tenantId={tenantId} onOnline={computerOnline} />
      : <AccessDenied what={tx('Yeni yedekleme bilgisayarı eklemek için çalışma alanı yöneticisi olmalısınız.')} control={<Link className={linkClass} to={base}>{tx('Ana sayfaya dön')}</Link>} />)}
    {phase === 'ready' && <ReadyStep settings={settings} pending={pending} failures={items.flatMap(item => results[item.key]?.error ? [results[item.key].error] : [])} onStart={() => void create(true)} base={base} />}
    {phase === 'running' && <ProgressStep items={items} names={itemName} runIds={Object.fromEntries(items.map(item => [item.key, results[item.key]?.runId ?? '']))} settings={settings} base={base} />}
    {!phase && step === 'data' && <DataStep agents={agents.data ?? []} names={names} onNames={setNames} picked={picked} onPicked={setPicked} onNext={chosen => { setItems(chosen); setStep('summary') }} />}
    {!phase && step === 'summary' && <SummaryStep items={items} names={names} settings={settings} onSettings={setSettings} results={results} pending={pending} onCreate={startFirstBackup => void create(startFirstBackup)} onBack={() => setStep('data')} />}
  </QueryState>
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Ana sayfa'), to: base }, { label: tx('İlk yedek') }]} title={tx('İlk yedeğinizi alın')} description={tx('Bilgisayarınızı seçin, yedeklenecek veriyi işaretleyin ve başlayın.')} />
    {canOperate && <Stepper steps={[tx('Bilgisayar'), tx('Ne yedeklensin'), tx('Özet ve başla'), tx('İlk yedek')]} current={phase ? 3 : step === 'computer' ? 0 : step === 'data' ? 1 : 2} />}
    {body}
  </div>
}
