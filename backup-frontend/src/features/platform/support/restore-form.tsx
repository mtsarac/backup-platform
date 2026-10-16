import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { StatusPill } from '@/components/data/status-pill'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { artifactSize } from '@/features/artifacts/format'
import { inputClass, selectClass } from '@/features/platform/styles'
import { isActiveRestore, restoreFailureMessage, safeRestoreCode } from '@/features/restores/presentation'
import type { SupportRestoreTargets, SupportSession } from './types'
import { useSupportRestore, useSupportRestoreActions, useSupportRestoreTargets } from './hooks'
import { supportScope } from './presentation'
import { SupportQueryState } from './support-query-state'

const customerWarning = 'Bu hedef müşterinin canlı (production) ortamı olabilir; izole bir Support Restore Environment değildir.'
export function SupportRestoreForm({ session, artifactId, onStarted }: { session: SupportSession; artifactId: string; onStarted: (id: string) => void }) {
  useLanguage()
  const scope = supportScope(session, 'restore')
  const targets = useSupportRestoreTargets(scope, artifactId)
  if (!scope.enabled) return <section><p role="status">{tx("Etkin oturum sahipliği ve RestoreBackup izni gerekir.")}</p><Button disabled>{tx("Precheck")}</Button><Button disabled>{tx("Restore başlat")}</Button></section>
  return <SupportQueryState isPending={targets.isPending} error={targets.error}>{targets.data && <RestoreForm key={`${session.id}:${session.tenantId}:${artifactId}:${session.status}`} session={session} artifactId={artifactId} targets={targets.data} onStarted={onStarted} />}</SupportQueryState>
}
function RestoreForm({ session, artifactId, targets, onStarted }: { session: SupportSession; artifactId: string; targets: SupportRestoreTargets; onStarted: (id: string) => void }) {
  useLanguage()
  const scope = supportScope(session, 'restore')
  const isPg = targets.engine === 'postgres'
  const engineName = isPg ? 'PostgreSQL' : 'SQL Server'
  const eligibleAgents = targets.agents.filter(agent => agent.canRestore && agent.canPrecheck && agent.sourceEligible && agent.status === 'Active' && agent.connectivity === 'online')
  const [agentId, setAgentId] = useState(eligibleAgents[0]?.agentId ?? '')
  const [targetJobId, setTargetJobId] = useState(targets.targets.find(target => target.agentId === eligibleAgents[0]?.agentId)?.jobId ?? '')
  const [databaseName, setDatabaseName] = useState(`${targets.sourceDatabase}_support`)
  const [verifyChecksum, setVerifyChecksum] = useState(true)
  const [verifyBackup, setVerifyBackup] = useState(true)
  const [acknowledged, setAcknowledged] = useState(false)
  const [checked, setChecked] = useState<{ id: string; fingerprint: string } | null>(null)
  const [validation, setValidation] = useState('')
  const precheckKey = useRef({ body: '', key: crypto.randomUUID() })
  const restoreKey = useRef({ body: '', key: crypto.randomUUID() })
  const submitting = useRef(false)
  const mounted = useRef(true)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const { precheck, start } = useSupportRestoreActions(scope, artifactId)
  const operation = useSupportRestore(scope, checked?.id ?? '')
  const input = { targetJobId, databaseName: databaseName.trim(), options: { verifyChecksum, verifyBackup } }
  const fingerprint = JSON.stringify(input)
  const target = targets.targets.find(item => item.jobId === targetJobId && item.agentId === agentId)
  const eligible = eligibleAgents.some(agent => agent.agentId === agentId) && Boolean(target) && targets.backupType === 'Full'
  const busy = precheck.isPending || start.isPending || Boolean(operation.data && isActiveRestore(operation.data.status))
  const matching = checked?.fingerprint === fingerprint
  const passed = matching && operation.data?.purpose === 'Precheck' && operation.data.status === 'Succeeded' && !operation.data.result?.preCheck.some(item => item.status === 'Failed')
  function runPrecheck() {
    if (!scope.enabled || !eligible || submitting.current || busy) return
    if (!databaseName.trim() || databaseName.trim().length > 128) { setValidation(tx('Yeni Database adı zorunludur ve en fazla 128 karakter olabilir.')); return }
    if (isPg && new TextEncoder().encode(databaseName.trim()).length > 63) { setValidation(tx('PostgreSQL Database adı en fazla 63 UTF-8 byte olabilir.')); return }
    setValidation('')
    submitting.current = true
    // Same request retains its key across failed transport retries. A completed check is a new operation.
    if (precheckKey.current.body !== fingerprint || (operation.data && !isActiveRestore(operation.data.status))) precheckKey.current = { body: fingerprint, key: crypto.randomUUID() }
    precheck.mutate({ input, idempotencyKey: precheckKey.current.key }, {
      onSuccess: result => { if (mounted.current) { setChecked({ id: result.id, fingerprint }); setAcknowledged(false) } },
      onSettled: () => { submitting.current = false },
    })
  }
  function startRestore() {
    if (!scope.enabled || !eligible || !passed || !acknowledged || !checked || submitting.current || busy || operation.error) return
    submitting.current = true
    const request = { ...input, precheckId: checked.id }
    const body = JSON.stringify(request)
    if (restoreKey.current.body !== body) restoreKey.current = { body, key: crypto.randomUUID() }
    start.mutate({ input: request, idempotencyKey: restoreKey.current.key }, { onSuccess: result => { if (mounted.current) onStarted(result.id) }, onSettled: () => { submitting.current = false } })
  }
  return <section aria-label={tx("Support Restore")}>
    <Section title={tx("Yeni Database geri yükleme")} description={<>{tx("Kaynak:")}{' '}{targets.sourceDatabase} · {artifactSize(targets.sizeBytes)}{' '}{tx("· NewDatabase")}</>}>
    <div className="space-y-5">
    <Stepper steps={[tx('Hedef ve Ön Kontrol'), tx('Sonucu onayla'), tx('Geri Yüklemeyi başlat')]} current={passed ? acknowledged ? 2 : 1 : 0} />
    <Notice tone="warning" title={tx(customerWarning)}>{targets.environment.warning && targets.environment.warning !== tx(customerWarning) && <p>{targets.environment.warning}</p>}<p>{tx("Hedef tenant'ın gerçek")}{' '}{engineName}{' '}{tx("bağlantısıdır. Mevcut Database'ler değiştirilmez; yalnızca yeni Database oluşturulur.")}</p>{isPg && <p>{tx("Yalnızca güvenilir platform arşivlerini kullanın: pg_restore arşiv SQL ve fonksiyonlarını hedefte superuser olarak çalıştırabilir. SHA-256 byte bütünlüğünü doğrular, SQL güvenliğini doğrulamaz. pg_dump cluster rollerini yedeklemez; kaynak sahiplikleri/ACL için gereken roller ve extension paketleri hazır olmalı; hedef sürümü hem kaynak hem pg_dump sürümünden eski olmamalıdır.")}</p>}</Notice>
    <fieldset disabled={busy || !scope.enabled} className="grid gap-4 sm:grid-cols-2">
      <Field label={tx("Agent")}>{(control) => <select {...control} className={`${selectClass} w-full`} value={agentId} onChange={event => { const id = event.target.value; setAgentId(id); setTargetJobId(targets.targets.find(item => item.agentId === id)?.jobId ?? ''); setAcknowledged(false) }}><option value="">{tx("Agent seçin")}</option>{eligibleAgents.map(agent => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? agent.agentId} · {agent.sourceAccess}</option>)}</select>}</Field>
      <Field label={tx("Hedef {0}", { "0": engineName })}>{(control) => <select {...control} className={`${selectClass} w-full`} value={targetJobId} onChange={event => { setTargetJobId(event.target.value); setAcknowledged(false) }}><option value="">{tx("Hedef seçin")}</option>{targets.targets.filter(item => item.agentId === agentId).map(item => <option key={item.jobId} value={item.jobId}>{item.jobName} · {item.host}{item.port ? `:${item.port}` : ''} · {item.database}</option>)}</select>}</Field>
      <div className="sm:col-span-2"><Field label={tx("Yeni Database adı")}>{(control) => <input {...control} className={inputClass} value={databaseName} maxLength={128} onChange={event => { setDatabaseName(event.target.value); setAcknowledged(false) }} />}</Field></div>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={verifyChecksum} onChange={event => { setVerifyChecksum(event.target.checked); setAcknowledged(false) }} />{tx("Checksum doğrula")}</label>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={verifyBackup} onChange={event => { setVerifyBackup(event.target.checked); setAcknowledged(false) }} />{tx("Backup doğrula ·")}{' '}{isPg ? tx('pg_restore arşiv doğrulaması') : 'RESTORE VERIFYONLY'}</label>
    </fieldset>
    {!eligible && <p role="status" className="text-sm">{tx("Uygun, çevrimiçi ve restore/precheck destekleyen bir Agent ve ona ait hedef gerekir. Yalnızca Full backup geri yüklenebilir.")}</p>}
    {validation && <p role="alert" className="text-sm text-error">{validation}</p>}
    <SupportQueryState isPending={false} error={precheck.error || start.error || operation.error} />
    <Button variant="outline" disabled={!eligible || busy || !scope.enabled || !databaseName.trim()} onClick={runPrecheck}>{busy && !start.isPending ? tx('Ön Kontrol çalışıyor…') : checked ? tx('Ön Kontrolü tekrar çalıştır') : tx('Ön Kontrol')}</Button>
    {checked && operation.data && <section aria-label={tx("Precheck sonucu")} className="space-y-2 rounded-lg border border-border bg-surface-2 p-4"><StatusPill status={operation.data.status} />{!matching && <p role="status">{tx("Ayarlar değişti. Start için yeniden başarılı Precheck gerekir.")}</p>}<ul className="space-y-1 text-sm">{operation.data.result?.preCheck.map((check, index) => <li key={`${check.checkId}:${index}`}>{safeRestoreCode(check.checkId)} · {tx(check.status)} · {check.status === 'Failed' ? restoreFailureMessage(check.reasonCode) : check.message || safeRestoreCode(check.reasonCode)}</li>)}</ul>{operation.data.failureCode && <p className="text-sm text-error">{restoreFailureMessage(operation.data.failureCode)}</p>}{passed && <p className="text-sm text-success">{tx("Precheck başarılı. Canlı ortam uyarısını onaylayarak başlatabilirsiniz.")}</p>}</section>}
    <label className="flex items-start gap-2 rounded-lg border border-warning/30 p-4 text-sm"><input type="checkbox" className="mt-0.5" checked={acknowledged} disabled={!scope.enabled || start.isPending} onChange={event => setAcknowledged(event.target.checked)} />{tx("Bu işlemin müşterinin canlı ortamında çalışabileceğini anladım ve yeni Database geri yüklemesini onaylıyorum.")}</label>
    <Button disabled={!scope.enabled || !eligible || !passed || !acknowledged || busy || Boolean(operation.error)} onClick={startRestore}>{start.isPending ? tx('Geri yükleme başlatılıyor…') : tx('Geri Yüklemeyi başlat')}</Button>
    </div>
    </Section>
  </section>
}
