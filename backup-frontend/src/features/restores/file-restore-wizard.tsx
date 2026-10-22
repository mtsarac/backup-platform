import { useEffect, useId, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { tx, useLanguage } from '@/lib/i18n'
import { ApiError } from '@/lib/api/client'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { inputClass, selectClass } from '@/features/platform/styles'
import { FilePathList, validFilePath, validFilePaths } from '@/features/backup-jobs/file-path-list'
import type { BackupArtifact } from '@/features/artifacts/types'
import { useRestore, useStartRestore, useStartRestorePrecheck } from './hooks'
import { PrecheckResults } from './precheck-results'
import { restoreErrorMessage, restoreFailureMessage } from './presentation'
import type { RestoreTargetsResponse, RestoreView, StartRestorePrecheckVariables, StartRestoreVariables } from './types'

const localTarget = { host: 'localhost', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false }

export function FileRestoreWizard({ tenantId, artifact, targets }: { tenantId: string; artifact: BackupArtifact; targets: RestoreTargetsResponse }) {
  useLanguage()
  const id = useId()
  const navigate = useNavigate()
  const originalTarget = targets.targets.find(target => target.jobId === artifact.jobId && target.database === targets.sourceDatabase)
  const originalAgent = targets.agents.find(agent => agent.agentId === originalTarget?.agentId && agent.canRestore && agent.canPrecheck && agent.sourceEligible)
  const [agentId, setAgentId] = useState(() => targets.agents.find(agent => agent.canRestore && agent.canPrecheck && agent.sourceEligible)?.agentId ?? '')
  const [location, setLocation] = useState<'original' | 'alternate'>('alternate')
  const [reference, setReference] = useState(targets.sourceDatabase)
  const [directory, setDirectory] = useState('restored')
  const [scope, setScope] = useState<'all' | 'selected'>('all')
  const [paths, setPaths] = useState<string[]>([])
  const [overwrite, setOverwrite] = useState(false)
  const [confirmation, setConfirmation] = useState('')
  const [precheck, setPrecheck] = useState<{ fingerprint: string; data: RestoreView } | null>(null)
  const [precheckError, setPrecheckError] = useState<unknown>(null)
  const [executionError, setExecutionError] = useState<unknown>(null)
  const precheckAttempt = useRef<{ fingerprint: string; variables: StartRestorePrecheckVariables } | null>(null)
  const executionAttempt = useRef<{ fingerprint: string; variables: StartRestoreVariables } | null>(null)
  const pending = useRef(false)
  const mounted = useRef(true)
  const startPrecheck = useStartRestorePrecheck()
  const startRestore = useStartRestore()
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])

  const original = location === 'original'
  const targetReference = original ? targets.sourceDatabase : reference
  const relativeDirectory = original ? '' : directory
  const request = {
    agentId, target: localTarget, databaseName: targetReference,
    mode: overwrite ? 'ReplaceExistingDatabase' as const : 'NewDatabase' as const,
    options: { terminateActiveConnections: false, verifyChecksum: true, verifyBackup: true, files: { paths: scope === 'all' ? null : paths, relativeDirectory } },
  }
  const fingerprint = JSON.stringify(request)
  const current = precheck?.fingerprint === fingerprint
  const query = useRestore(current && precheck ? precheck.data.id : '')
  const result = current && precheck ? query.data ?? precheck.data : undefined
  const failed = result?.status === 'Failed' || result?.result?.preCheck?.some(item => item.status === 'Failed') === true
  const passed = result?.purpose === 'Precheck' && result.status === 'Succeeded' && !failed
  const agent = targets.agents.find(item => item.agentId === agentId)
  const referenceValid = /^[A-Za-z0-9_.-]{1,128}$/.test(targetReference)
  const eligible = artifact.state === 'Available' && agent?.canRestore && agent.canPrecheck && agent.sourceEligible && (!original || agent.agentId === originalAgent?.agentId)
  const directoryValid = original || directory === '' || validFilePath(directory)
  const pathsValid = scope === 'all' || validFilePaths(paths)
  const confirmed = !overwrite || confirmation === targetReference
  const valid = Boolean(eligible && referenceValid && directoryValid && pathsValid && confirmed)
  const busy = startPrecheck.isPending || startRestore.isPending

  async function check(fresh = false) {
    if (!valid || pending.current) return
    const previous = precheckAttempt.current
    const attempt = !fresh && previous?.fingerprint === fingerprint && precheckError != null && (!(precheckError instanceof ApiError) || precheckError.status >= 500) ? previous : {
      fingerprint, variables: { artifactId: artifact.id, request, idempotencyKey: crypto.randomUUID() },
    }
    precheckAttempt.current = attempt
    executionAttempt.current = null
    setPrecheck(null)
    setPrecheckError(null)
    setExecutionError(null)
    pending.current = true
    try {
      const data = await startPrecheck.mutateAsync(attempt.variables)
      if (mounted.current) setPrecheck({ fingerprint: attempt.fingerprint, data })
    } catch (error) {
      if (mounted.current) setPrecheckError(error)
    } finally { pending.current = false }
  }

  async function restore() {
    if (!valid || !passed || !current || !precheck || pending.current) return
    const executionFingerprint = JSON.stringify([fingerprint, precheck.data.id, confirmation])
    const previous = executionAttempt.current
    const attempt = previous?.fingerprint === executionFingerprint && executionError != null && (!(executionError instanceof ApiError) || executionError.status >= 500) ? previous : {
      fingerprint: executionFingerprint, variables: { artifactId: artifact.id, idempotencyKey: crypto.randomUUID(), request: { ...request, precheckId: precheck.data.id, ...(overwrite ? { confirmOverwrite: confirmation } : {}) } },
    }
    executionAttempt.current = attempt
    setExecutionError(null)
    pending.current = true
    try {
      const data = await startRestore.mutateAsync(attempt.variables)
      if (mounted.current) navigate(`/tenants/${tenantId}/restores/${data.id}`)
    } catch (error) {
      if (mounted.current) setExecutionError(error)
    } finally { pending.current = false }
  }

  return <div className="space-y-5">
    <Notice tone="info">{tx('Dosyalar yalnızca Agent üzerinde yapılandırılmış köklere geri yüklenir. Özgün konum kaynak referansının köküdür; alternatif konum başka bir kaynak referansı ve/veya bu kök altında göreli klasördür. Özgün fiziksel konum için kaynak Agent ve değişmemiş kaynak eşlemesi gerekir. Dosya izinleri, zaman damgaları ve SHA-256 bütünlüğü korunur.')}</Notice>
    <Section title={tx('Dosya / klasör geri yükleme')}>
      <fieldset disabled={busy} className="space-y-4">
        <Field id={`${id}-agent`} label={tx('Agent')} required>{control => <select {...control} className={selectClass} value={agentId} onChange={event => { setAgentId(event.target.value); setConfirmation('') }}>
          <option value="">{tx('Agent seçin')}</option>
          {targets.agents.map(item => <option key={item.agentId} value={item.agentId} disabled={!item.canRestore || !item.canPrecheck || !item.sourceEligible || (original && item.agentId !== originalAgent?.agentId)}>{item.hostName ?? item.agentId}</option>)}
        </select>}</Field>
        {agent && !eligible && <Notice tone="error">{restoreFailureMessage(agent.rejectionCode)}</Notice>}
        <fieldset className="flex flex-wrap gap-4">
          <legend className="mb-2 text-sm font-medium">{tx('Geri yükleme konumu')}</legend>
          <label className="flex items-center gap-2"><input type="radio" name={`${id}-location`} checked={original} disabled={!originalAgent} onChange={() => { if (originalAgent) { setLocation('original'); setAgentId(originalAgent.agentId); setConfirmation('') } }} />{tx('Özgün konum')}</label>
          <label className="flex items-center gap-2"><input type="radio" name={`${id}-location`} checked={!original} onChange={() => { setLocation('alternate'); setConfirmation('') }} />{tx('Alternatif konum')}</label>
        </fieldset>
        {!originalAgent && <p className="text-sm text-text-muted">{tx('Özgün konum için kaynak işin Agent eşlemesi ve uygun bir Agent gerekir. Alternatif konum kullanabilirsiniz.')}</p>}
        {original ? <p className="text-sm">{tx('Dosya kaynağı referansı')}: <code>{targets.sourceDatabase}</code></p> : <div className="grid gap-4 sm:grid-cols-2">
          <Field id={`${id}-reference`} label={tx('Hedef dosya kaynağı referansı')} required error={!referenceValid ? tx('Geçerli bir dosya kaynağı referansı girin.') : undefined}>{control => <input {...control} className={inputClass} value={reference} onChange={event => { setReference(event.target.value); setConfirmation('') }} />}</Field>
          <Field id={`${id}-directory`} label={tx('Hedef göreli klasör')} hint={tx('Boş değer veya . yapılandırılmış kökü kullanır; örnek: restored/2026.')} error={!directoryValid ? tx('Geçerli bir göreli yol girin.') : undefined}>{control => <input {...control} className={inputClass} value={directory} onChange={event => { setDirectory(event.target.value); setConfirmation('') }} />}</Field>
        </div>}
        <fieldset className="flex flex-wrap gap-4">
          <legend className="mb-2 text-sm font-medium">{tx('Geri yükleme kapsamı')}</legend>
          <label className="flex items-center gap-2"><input type="radio" name={`${id}-scope`} checked={scope === 'all'} onChange={() => setScope('all')} />{tx('Tüm dosya ve klasörler')}</label>
          <label className="flex items-center gap-2"><input type="radio" name={`${id}-scope`} checked={scope === 'selected'} onChange={() => setScope('selected')} />{tx('Seçili dosya ve klasörler')}</label>
        </fieldset>
        {scope === 'selected' && <FilePathList id={`${id}-paths`} label={tx('Geri yüklenecek göreli yollar')} paths={paths} onChange={setPaths} error={!pathsValid ? tx('1–256 geçerli göreli yol girin.') : undefined} />}
        <p className="text-sm text-text-muted">{tx('Seçilen klasörlerin altındaki dosyalar da geri yüklenir; arşivdeki göreli dizin yapısı korunur. Arşivde bulunmayan seçimler işlemi durdurur.')}</p>
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={overwrite} onChange={event => { setOverwrite(event.target.checked); setConfirmation('') }} />{tx('Mevcut dosyaların üzerine yazmaya izin ver')}</label>
        {overwrite && <>
          <Notice tone="warning">{tx('Seçili hedefteki çakışan dosyalar değiştirilecektir. Başlatmadan önce kaynak referansını, göreli klasörü ve kapsamı kontrol edin.')}</Notice>
          <Field id={`${id}-confirmation`} label={tx('Üzerine yazmayı onaylamak için hedef kaynak referansını yazın: {0}', { '0': targetReference })} error={confirmation && !confirmed ? tx('Girdiğiniz kaynak referansı eşleşmiyor.') : undefined}>{control => <input {...control} className={inputClass} value={confirmation} onChange={event => setConfirmation(event.target.value)} autoComplete="off" />}</Field>
        </>}
      </fieldset>
    </Section>
    <Section title={tx('Başlatmadan önce')}>
      <Facts>
        <Fact label={tx('Kaynak')}>{targets.sourceDatabase}</Fact>
        <Fact label={tx('Hedef dosya kaynağı referansı')}>{targetReference}</Fact>
        <Fact label={tx('Hedef göreli klasör')}>{relativeDirectory || '.'}</Fact>
        <Fact label={tx('Geri yükleme kapsamı')}>{scope === 'all' ? tx('Tüm dosya ve klasörler') : paths.join(', ')}</Fact>
        <Fact label={tx('Dosya ve manifest bütünlüğü (SHA-256)')}>{tx('Zorunlu')}</Fact>
      </Facts>
    </Section>
    {precheckError != null && <Notice tone="error">{restoreErrorMessage(precheckError)}</Notice>}
    {current && query.error && <Notice tone="error">{restoreErrorMessage(query.error)}</Notice>}
    {result && <Section title={tx('Ön Kontrol (Precheck) Sonuçları')}><PrecheckResults fileArchive data={result} failed={failed} passed={passed} onRecheck={() => void check(true)} recheckDisabled={busy || !valid} /></Section>}
    {executionError != null && <Notice tone="error">{restoreErrorMessage(executionError)}</Notice>}
    <div className="flex flex-wrap justify-end gap-3">
      <Button type="button" variant="outline" disabled={!valid || busy} onClick={() => void check()}>{startPrecheck.isPending ? tx('Ön Kontrol Başlatılıyor…') : tx('Ön Kontrolü Çalıştır →')}</Button>
      <Button type="button" variant={overwrite ? 'destructive' : 'default'} disabled={!valid || !passed || busy} onClick={() => void restore()}>{startRestore.isPending ? tx('Geri Yükleme Başlatılıyor…') : tx('Dosyaları geri yükle')}</Button>
    </div>
  </div>
}
