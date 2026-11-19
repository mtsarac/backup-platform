import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { AccessDenied } from '@/components/auth/access-denied'
import { QueryState } from '@/components/data/query-state'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { Stepper } from '@/components/ui/stepper'
import { AddAgentDialog } from '@/features/agents/add-agent-dialog'
import { useAgents } from '@/features/agents/hooks'
import { useRunBackupJob, useSaveBackupJob } from '@/features/backup-jobs/hooks'
import { DataStep } from '@/features/first-backup/data-step'
import { autoJobName, defaultSettings, firstBackupJobInput, type FirstBackupItem, type FirstBackupSettings } from '@/features/first-backup/first-backup-model'
import { SummaryStep, type ItemResult } from '@/features/first-backup/summary-step'
import { useTenant } from '@/features/tenancy/use-tenant'

type Step = 'computer' | 'data' | 'summary'

/** `…/first-backup`: add a computer when there is none, then choose what to back up, review, and create one job per pick (optionally starting its first backup). */
export function FirstBackupPage() {
  useLanguage()
  const { tenantId, canOperate, canAdmin } = useTenant()
  const base = `/tenants/${tenantId}`
  const navigate = useNavigate()
  const agents = useAgents(tenantId)
  const save = useSaveBackupJob()
  const { run } = useRunBackupJob()
  const [step, setStep] = useState<Step | null>(null)
  const [picked, setPicked] = useState<ReadonlySet<string> | null>(null)
  const [names, setNames] = useState<Record<string, string>>({})
  const [items, setItems] = useState<FirstBackupItem[]>([])
  const [settings, setSettings] = useState<FirstBackupSettings>(defaultSettings)
  const [results, setResults] = useState<Record<string, ItemResult>>({})
  const [pending, setPending] = useState(false)
  const current: Step | null = step ?? (agents.data ? (agents.data.length ? 'data' : 'computer') : null)

  async function afterComputer() {
    const { data } = await agents.refetch()
    if (data?.length) setStep('data')
    else void navigate(base)
  }
  async function create(startFirstBackup: boolean) {
    setPending(true)
    const outcome = { ...results }
    for (const item of items) {
      const previous = outcome[item.key] ?? {}
      let next: ItemResult = { jobId: previous.jobId, runId: previous.runId }
      try {
        if (!next.jobId) next.jobId = (await save.mutateAsync(firstBackupJobInput(item, names[item.key]?.trim() || autoJobName(item.database), settings, canAdmin))).id
        if (startFirstBackup && !next.runId) next.runId = (await run(next.jobId))?.id
      } catch (error) { next = { ...next, error } }
      outcome[item.key] = next
      setResults({ ...outcome })
    }
    setPending(false)
    if (items.every(item => outcome[item.key].jobId && !outcome[item.key].error)) void navigate(base)
  }

  let body
  if (!canOperate) body = <AccessDenied what={tx('Bu sayfada değişiklik yapma yetkiniz yok. Viewer rolü salt okunurdur.')} />
  else body = <QueryState isPending={agents.isPending} error={agents.error}>
    {current === 'computer' && (canAdmin
      ? <AddAgentDialog tenantId={tenantId} onClose={() => void afterComputer()} />
      : <AccessDenied what={tx('Yeni yedekleme bilgisayarı eklemek için çalışma alanı yöneticisi olmalısınız.')} control={<Link className={linkClass} to={base}>{tx('Ana sayfaya dön')}</Link>} />)}
    {current === 'data' && <DataStep agents={agents.data ?? []} names={names} onNames={setNames} picked={picked} onPicked={setPicked} onNext={chosen => { setItems(chosen); setStep('summary') }} />}
    {current === 'summary' && <SummaryStep items={items} names={names} settings={settings} onSettings={setSettings} results={results} pending={pending} onCreate={startFirstBackup => void create(startFirstBackup)} onBack={() => setStep('data')} />}
  </QueryState>
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Ana sayfa'), to: base }, { label: tx('İlk yedek') }]} title={tx('İlk yedeğinizi alın')} description={tx('Bilgisayarınızı seçin, yedeklenecek veriyi işaretleyin ve başlayın.')} />
    {canOperate && <Stepper steps={[tx('Bilgisayar'), tx('Ne yedeklensin'), tx('Özet ve başla'), tx('İlk yedek')]} current={current === 'computer' ? 0 : current === 'data' ? 1 : 2} />}
    {body}
  </div>
}
