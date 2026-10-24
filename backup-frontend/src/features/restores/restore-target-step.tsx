import { tx, useLanguage } from '@/lib/i18n'
import { CheckCircle2, XCircle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'
import { inputClass, selectClass } from '@/components/ui/form-styles'
import { StoredLoginPanel } from './stored-login-panel'
import type { RestoreTargetAgent, RestoreTarget, RestoreTargetsResponse } from './types'
import { failureMessage } from '@/lib/api/failure-messages'

interface RestoreTargetStepProps {
  tenantId: string
  instancePrefix: string
  isPg: boolean
  engineName: string
  targets: RestoreTargetsResponse
  activeAgent: RestoreTargetAgent | undefined
  selectedAgentId: string
  targetType: 'configured' | 'custom'
  selectedTargetIndex: number
  customTarget: RestoreTarget
  eligibleForRestore: boolean
  targetValid: boolean
  invalidateDiscovery: () => void
  setSelectedAgentId: (id: string) => void
  setTargetType: (type: 'configured' | 'custom') => void
  setSelectedTargetIndex: (index: number) => void
  changeCustomTarget: (value: RestoreTarget) => void
  onContinue: () => void
}

export function RestoreTargetStep({ tenantId, instancePrefix, isPg, engineName, targets, activeAgent, selectedAgentId, targetType, selectedTargetIndex, customTarget, eligibleForRestore, targetValid, invalidateDiscovery, setSelectedAgentId, setTargetType, setSelectedTargetIndex, changeCustomTarget, onContinue }: RestoreTargetStepProps) {
  useLanguage()
  return (
    <div className="space-y-5">
      <Section title={tx("1. Hedef Agent Seçimi")} description={tx("Backup dosyasını alıp {0} sunucusuna aktaracak Agent'ı seçin.", { "0": engineName })}>
        <div className="space-y-3">
          <label htmlFor={`${instancePrefix}-agent`} className="block text-sm font-medium">{tx("Agent")}</label>
          <select
            id={`${instancePrefix}-agent`}
            className={`${selectClass} w-full`}
            value={selectedAgentId}
            onChange={(e) => { invalidateDiscovery(); setSelectedAgentId(e.target.value) }}
          >
            {targets.agents.map((ag) => (
              <option key={ag.agentId} value={ag.agentId}>
                {ag.hostName ?? ag.agentId} ({tx(ag.connectivity)}) — {ag.canRestore ? tx('Restore destekliyor') : tx('Restore desteği yok')}
              </option>
            ))}
          </select>

          {activeAgent && (
            <div className={`rounded-md p-3 text-sm ${activeAgent.sourceEligible && activeAgent.canRestore ? 'bg-success-subtle text-success' : 'bg-error-subtle text-error'}`}>
              {activeAgent.sourceEligible && activeAgent.canRestore ? (
                <div className="flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <CheckCircle2 className="size-4 shrink-0" />
                    <span>{tx("Bu Agent bu backup için uygun (")}{activeAgent.sourceAccess === 'AgentLocal' ? tx('Yerel Replika') : activeAgent.sourceAccess === 'BackendRelayed' ? tx('Uzak Depolama (SFTP / Google Drive Relay)') : tx('Merkezi Depolama')}).</span>
                  </div>
                  <p className="ml-6 text-xs text-text-muted">{activeAgent.sourceAccess === 'AgentLocal'
                    ? tx('Yerel replika yalnızca onu barındıran hedef Agent tarafından okunabilir; backend yerel dosyayı kontrol edemez.')
                    : tx("Kaynak erişimi kontrol edilirken, Agent'a devredilmeden önce backend'in okuyabildiği alternatif doğrulanmış replika seçilebilir.")}{' '}{tx("Agent'a devir, staging veya hedef değişikliği sonrasında Restore otomatik yeniden başlatılmaz.")}</p>
                </div>
              ) : (
                <div className="flex items-start gap-2">
                  <XCircle className="size-4 shrink-0 mt-0.5" />
                  <div>
                    <p className="font-medium">{tx("Bu Agent seçilemez veya backup'a erişemez:")}</p>
                    <p className="mt-1 text-sm">{failureMessage(activeAgent.rejectionCode, 'restore')}</p>
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </Section>

      <Section title={tx("2. Hedef {0}", { "0": engineName })} description={tx("Geri yüklemenin yapılacağı {0} bağlantı referansı.", { "0": engineName })}>
        <div className="space-y-4">
          <div className="flex flex-wrap gap-4 text-sm">
            <label className="flex items-center gap-2">
              <input
                type="radio"
                name="targetType"
                checked={targetType === 'configured'}
                onChange={() => { invalidateDiscovery(); setTargetType('configured') }}
                disabled={targets.targets.length === 0}
              />
              <span>{tx("Kayıtlı İş Bağlantısı (")}{targets.targets.length})</span>
            </label>
            <label className="flex items-center gap-2">
              <input
                type="radio"
                name="targetType"
                checked={targetType === 'custom'}
                onChange={() => { invalidateDiscovery(); setTargetType('custom') }}
              />
              <span>{tx("Özel Bağlantı")}</span>
            </label>
          </div>

          {targetType === 'configured' && targets.targets.length > 0 && (
            <div>
              <label htmlFor={`${instancePrefix}-target-select`} className="block text-sm font-medium">{tx("Bağlantı")}</label>
              <select
                id={`${instancePrefix}-target-select`}
                className={`${selectClass} w-full`}
                value={selectedTargetIndex}
                onChange={(e) => { invalidateDiscovery(); setSelectedTargetIndex(Number(e.target.value)) }}
              >
                {targets.targets.map((t, idx) => (
                  <option key={`${t.jobId}-${idx}`} value={idx}>
                    {t.jobName} · {t.target.host}{t.target.port ? `:${t.target.port}` : ''} ({t.database})
                  </option>
                ))}
              </select>
            </div>
          )}

          {targetType === 'custom' && (
            <div className="grid gap-3 sm:grid-cols-2">
              <div>
                <label htmlFor={`${instancePrefix}-host`} className="block text-sm font-medium">{isPg ? tx('PostgreSQL Host') : tx('Host / Instance')}</label>
                <input
                  id={`${instancePrefix}-host`}
                  className={inputClass}
                  value={customTarget.host}
                  onChange={(e) => changeCustomTarget({ ...customTarget, host: e.target.value })}
                  placeholder={isPg ? 'localhost' : tx('localhost veya SQLSERVER')}
                />
              </div>
              <div>
                <label htmlFor={`${instancePrefix}-port`} className="block text-sm font-medium">{tx("Port (İsteğe bağlı)")}</label>
                <input
                  id={`${instancePrefix}-port`}
                  type="number"
                  className={inputClass}
                  value={customTarget.port ?? ''}
                  onChange={(e) => changeCustomTarget({ ...customTarget, port: e.target.value ? Number(e.target.value) : null })}
                  placeholder={isPg ? '5432' : '1433'}
                />
              </div>
              {!customTarget.integratedSecurity && <StoredLoginPanel tenantId={tenantId} agentId={selectedAgentId} idPrefix={`${instancePrefix}-login`} reference={customTarget.databaseCredentialReference} onStored={(reference) => changeCustomTarget({ ...customTarget, databaseCredentialReference: reference })} />}
              <div className="flex flex-wrap items-center gap-4 text-sm sm:col-span-2">
                                    <label className="flex items-center gap-2">
                  <input type="checkbox" checked={customTarget.integratedSecurity} onChange={(e) => changeCustomTarget({ ...customTarget, integratedSecurity: e.target.checked, databaseCredentialReference: null })} />
                  <span>{tx("Windows kimlik doğrulaması")}</span>
                </label>
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={customTarget.encrypt}
                    onChange={(e) => changeCustomTarget({ ...customTarget, encrypt: e.target.checked })}
                  />
                  <span>{tx("Bağlantıyı Şifrele")}</span>
                </label>
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={customTarget.trustServerCertificate}
                    onChange={(e) => changeCustomTarget({ ...customTarget, trustServerCertificate: e.target.checked })}
                  />
                  <span>{tx("Sunucu Sertifikasına Güven")}</span>
                </label>
              </div>
            </div>
          )}
        </div>
      </Section>

      <div className="flex justify-end gap-3">
        <Button
          disabled={!eligibleForRestore || !targetValid}
          onClick={() => onContinue()}
        >{tx("Devam et: Restore Modu →")}</Button>
      </div>
    </div>
  )
}
