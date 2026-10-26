import { useState } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { CheckCircle2, XCircle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
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
  const [loginPending, setLoginPending] = useState(false)
  return (
    <div className="space-y-5">
      <Section title={tx("Hangi yedekleme bilgisayarı geri yüklesin?")} description={tx("Neden soruluyor? Yedek dosyasını alıp {0} sunucusuna aktaracak yedekleme bilgisayarını seçmeniz gerekir.", { "0": engineName })}>
        <div className="space-y-3">
          <label htmlFor={`${instancePrefix}-agent`} className="block text-sm font-medium">{tx("Yedekleme bilgisayarı")}</label>
          <select
            id={`${instancePrefix}-agent`}
            className={`${selectClass} w-full`}
            value={selectedAgentId}
            onChange={(e) => { invalidateDiscovery(); setSelectedAgentId(e.target.value) }}
          >
            {targets.agents.map((ag) => (
              <option key={ag.agentId} value={ag.agentId}>
                {ag.hostName ?? ag.agentId} ({tx(ag.connectivity)}) — {ag.canRestore ? tx('Geri yüklemeyi destekliyor') : tx('Geri yükleme desteği yok')}
              </option>
            ))}
          </select>

          {activeAgent && (
            <div className={`rounded-md p-3 text-sm ${activeAgent.sourceEligible && activeAgent.canRestore ? 'bg-success-subtle text-success' : 'bg-error-subtle text-error'}`}>
              {activeAgent.sourceEligible && activeAgent.canRestore ? (
                <div className="flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <CheckCircle2 aria-hidden="true" className="size-4 shrink-0" />
                    <span>{tx("Bu bilgisayar bu yedek dosyasına ulaşabilir.")}</span>
                  </div>
                  <p className="ml-6 text-xs text-text-muted">{activeAgent.sourceAccess === 'AgentLocal'
                    ? tx('Yedek yalnızca bu bilgisayarın kendi diskinde olduğu için geri yüklemeyi başka bir bilgisayar yapamaz.')
                    : tx('Bilgisayar veya hedef değişirse geri yükleme kendiliğinden yeniden başlamaz.')}</p>
                </div>
              ) : (
                <div className="flex items-start gap-2">
                  <XCircle aria-hidden="true" className="size-4 shrink-0 mt-0.5" />
                  <div>
                    <p className="font-medium">{tx("Bu bilgisayar seçilemez veya yedek dosyasına ulaşamaz:")}</p>
                    <p className="mt-1 text-sm">{failureMessage(activeAgent.rejectionCode, 'restore')}</p>
                  </div>
                </div>
              )}
            </div>
          )}
          {activeAgent && <TechnicalDetails items={[
            { label: tx('Yedekleme bilgisayarı kimliği'), value: activeAgent.agentId },
            ...(activeAgent.sourceAccess ? [{ label: tx('Yedek erişim yolu'), value: activeAgent.sourceAccess }] : []),
          ]} />}
        </div>
      </Section>

      <Section title={tx("Hangi {0} sunucusuna geri yüklensin?", { "0": engineName })} description={tx("Neden soruluyor? Geri yüklemenin yapılacağı {0} sunucusunun bağlantı bilgisi gerekir.", { "0": engineName })}>
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
              <span>{tx("Yedekleme işlerinde kayıtlı bağlantı ({0})", { "0": targets.targets.length })}</span>
            </label>
            <label className="flex items-center gap-2">
              <input
                type="radio"
                name="targetType"
                checked={targetType === 'custom'}
                onChange={() => { invalidateDiscovery(); setTargetType('custom') }}
              />
              <span>{tx("Başka bir sunucu")}</span>
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
              {!customTarget.integratedSecurity && <StoredLoginPanel tenantId={tenantId} agentId={selectedAgentId} idPrefix={`${instancePrefix}-login`} reference={customTarget.databaseCredentialReference} onPendingChange={setLoginPending} onStored={(reference) => changeCustomTarget({ ...customTarget, databaseCredentialReference: reference })} />}
              <label className="flex items-center gap-2 text-sm sm:col-span-2">
                <input type="checkbox" checked={customTarget.integratedSecurity} onChange={(e) => changeCustomTarget({ ...customTarget, integratedSecurity: e.target.checked, databaseCredentialReference: null })} />
                <span>{tx("Windows kimlik doğrulaması")}</span>
              </label>
              <details className="rounded-lg border border-border bg-surface-2 text-sm sm:col-span-2">
                <summary className="cursor-pointer select-none rounded-lg px-3 py-2 font-medium text-text-heading">{tx("Gelişmiş bağlantı ayarları")}</summary>
                <div className="flex flex-wrap items-center gap-4 border-t border-border px-3 py-2">
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
              </details>
            </div>
          )}
        </div>
      </Section>

      <div className="flex justify-end gap-3">
        <Button
          disabled={!eligibleForRestore || !targetValid || loginPending}
          onClick={() => onContinue()}
        >{tx("Devam et →")}</Button>
      </div>
    </div>
  )
}
