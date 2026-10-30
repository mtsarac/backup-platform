import { tx, useLanguage } from '@/lib/i18n'
import { RadioField } from '@/components/ui/radio-field'
import { CheckField } from '@/components/ui/check-field'
import { Select, TextInput } from '@/components/ui/text-input'
import { Section } from '@/components/ui/section'
import { StoredLoginPanel } from './stored-login-panel'
import type { RestoreTarget, RestoreTargetsResponse } from './types'

interface RestoreTargetStepProps {
  tenantId: string
  instancePrefix: string
  isPg: boolean
  engineName: string
  targets: RestoreTargetsResponse
  selectedAgentId: string
  targetType: 'configured' | 'custom'
  selectedTargetIndex: number
  customTarget: RestoreTarget
  invalidateDiscovery: () => void
  setTargetType: (type: 'configured' | 'custom') => void
  setSelectedTargetIndex: (index: number) => void
  changeCustomTarget: (value: RestoreTarget) => void
  onLoginPendingChange: (pending: boolean) => void
}

/** Which database server receives the restore; the database name and mode are asked next, on the same step. */
export function RestoreTargetStep({ tenantId, instancePrefix, isPg, engineName, targets, selectedAgentId, targetType, selectedTargetIndex, customTarget, invalidateDiscovery, setTargetType, setSelectedTargetIndex, changeCustomTarget, onLoginPendingChange }: RestoreTargetStepProps) {
  useLanguage()
  return (
    <Section title={tx("Hangi {0} sunucusuna geri yüklensin?", { "0": engineName })} description={tx("Neden soruluyor? Geri yüklemenin yapılacağı {0} sunucusunun bağlantı bilgisi gerekir.", { "0": engineName })}>
      <div className="space-y-4">
        <div className="flex flex-wrap gap-4 text-sm">
          <RadioField label={tx("Yedekleme işlerinde kayıtlı bağlantı ({0})", { "0": targets.targets.length })} name="targetType"
              checked={targetType === 'configured'}
              onChange={() => { invalidateDiscovery(); setTargetType('configured') }}
              disabled={targets.targets.length === 0} />
          <RadioField label={tx("Başka bir sunucu")} name="targetType"
              checked={targetType === 'custom'}
              onChange={() => { invalidateDiscovery(); setTargetType('custom') }} />
        </div>

        {targetType === 'configured' && targets.targets.length > 0 && (
          <div>
            <label htmlFor={`${instancePrefix}-target-select`} className="block text-sm font-medium">{tx("Bağlantı")}</label>
            <Select
              id={`${instancePrefix}-target-select`}
              value={selectedTargetIndex}
              onChange={(e) => { invalidateDiscovery(); setSelectedTargetIndex(Number(e.target.value)) }}
            >
              {targets.targets.map((t, idx) => (
                <option key={`${t.jobId}-${idx}`} value={idx}>
                  {t.jobName} · {t.target.host}{t.target.port ? `:${t.target.port}` : ''} ({t.database})
                </option>
              ))}
            </Select>
          </div>
        )}

        {targetType === 'custom' && (
          <div className="grid gap-3 sm:grid-cols-2">
            <div>
              <label htmlFor={`${instancePrefix}-host`} className="block text-sm font-medium">{isPg ? tx('PostgreSQL Host') : tx('Host / Instance')}</label>
              <TextInput
                id={`${instancePrefix}-host`}
                value={customTarget.host}
                onChange={(e) => changeCustomTarget({ ...customTarget, host: e.target.value })}
                placeholder={isPg ? 'localhost' : tx('localhost veya SQLSERVER')}
              />
            </div>
            <div>
              <label htmlFor={`${instancePrefix}-port`} className="block text-sm font-medium">{tx("Port (İsteğe bağlı)")}</label>
              <TextInput
                id={`${instancePrefix}-port`}
                type="number"
                value={customTarget.port ?? ''}
                onChange={(e) => changeCustomTarget({ ...customTarget, port: e.target.value ? Number(e.target.value) : null })}
                placeholder={isPg ? '5432' : '1433'}
              />
            </div>
            {!customTarget.integratedSecurity && <StoredLoginPanel tenantId={tenantId} agentId={selectedAgentId} idPrefix={`${instancePrefix}-login`} reference={customTarget.databaseCredentialReference} onPendingChange={onLoginPendingChange} onStored={(reference) => changeCustomTarget({ ...customTarget, databaseCredentialReference: reference })} />}
            <CheckField label={tx("Windows kimlik doğrulaması")} className="sm:col-span-2" checked={customTarget.integratedSecurity} onChange={(e) => changeCustomTarget({ ...customTarget, integratedSecurity: e.target.checked, databaseCredentialReference: null })} />
            <details className="rounded-lg border border-border bg-surface-2 text-sm sm:col-span-2">
              <summary className="cursor-pointer select-none rounded-lg px-3 py-2 font-medium text-text-heading">{tx("Gelişmiş bağlantı ayarları")}</summary>
              <div className="flex flex-wrap items-center gap-4 border-t border-border px-3 py-2">
                <CheckField label={tx("Bağlantıyı Şifrele")} checked={customTarget.encrypt}
                    onChange={(e) => changeCustomTarget({ ...customTarget, encrypt: e.target.checked })} />
                <CheckField label={tx("Sunucu Sertifikasına Güven")} checked={customTarget.trustServerCertificate}
                    onChange={(e) => changeCustomTarget({ ...customTarget, trustServerCertificate: e.target.checked })} />
              </div>
            </details>
          </div>
        )}
      </div>
    </Section>
  )
}
