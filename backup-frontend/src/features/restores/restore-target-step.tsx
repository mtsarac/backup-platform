import { tx, useLanguage } from '@/lib/i18n'
import { RadioField } from '@/components/ui/radio-field'
import { CheckField } from '@/components/ui/check-field'
import { TextInput } from '@/components/ui/text-input'
import { Section } from '@/components/ui/section'
import { StoredLoginPanel } from './stored-login-panel'
import type { RestoreTarget } from './types'

interface RestoreTargetStepProps {
  tenantId: string
  instancePrefix: string
  isPg: boolean
  engineName: string
  /** The server the backup was taken from; absent when its job no longer offers one. */
  ownTarget: RestoreTarget | undefined
  selectedAgentId: string
  targetType: 'own' | 'other'
  otherTarget: RestoreTarget
  invalidateDiscovery: () => void
  setTargetType: (type: 'own' | 'other') => void
  changeOtherTarget: (value: RestoreTarget) => void
  onLoginPendingChange: (pending: boolean) => void
}

/** Which database server receives the restore: the backup's own, or another one asked only for its address and, if needed, a login. */
export function RestoreTargetStep({ tenantId, instancePrefix, isPg, engineName, ownTarget, selectedAgentId, targetType, otherTarget, invalidateDiscovery, setTargetType, changeOtherTarget, onLoginPendingChange }: RestoreTargetStepProps) {
  useLanguage()
  const needsLogin = isPg || !otherTarget.integratedSecurity
  return (
    <Section title={tx("Hangi {0} sunucusuna geri yüklensin?", { "0": engineName })} description={tx("Neden soruluyor? Geri yüklemenin yapılacağı {0} sunucusunun bağlantı bilgisi gerekir.", { "0": engineName })}>
      <div className="space-y-4">
        <div className="flex flex-wrap gap-4 text-sm">
          <RadioField label={ownTarget ? tx("Yedeğin alındığı sunucu ({0})", { "0": `${ownTarget.host}${ownTarget.port ? `:${ownTarget.port}` : ''}` }) : tx("Yedeğin alındığı sunucu")} name="targetType"
              checked={targetType === 'own'}
              onChange={() => { invalidateDiscovery(); setTargetType('own') }}
              disabled={!ownTarget} />
          <RadioField label={tx("Başka sunucu")} name="targetType"
              checked={targetType === 'other'}
              onChange={() => { invalidateDiscovery(); setTargetType('other') }} />
        </div>

        {targetType === 'other' && (
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="sm:col-span-2">
              <label htmlFor={`${instancePrefix}-host`} className="block text-sm font-medium">{isPg ? tx('PostgreSQL Host') : tx('Host / Instance')}</label>
              <TextInput
                id={`${instancePrefix}-host`}
                value={otherTarget.host}
                onChange={(e) => changeOtherTarget({ ...otherTarget, host: e.target.value })}
                placeholder={isPg ? 'localhost' : tx('localhost veya SQLSERVER')}
              />
            </div>
            {!isPg && <CheckField label={tx("Kullanıcı adı ve parola gerekiyor")} className="sm:col-span-2" checked={!otherTarget.integratedSecurity} onChange={(e) => changeOtherTarget({ ...otherTarget, integratedSecurity: !e.target.checked, databaseCredentialReference: null })} />}
            <CheckField label={tx("Bağlantıyı şifrele")} className="sm:col-span-2" checked={otherTarget.encrypt} onChange={(e) => changeOtherTarget({ ...otherTarget, encrypt: e.target.checked })} />
            {needsLogin && <StoredLoginPanel tenantId={tenantId} agentId={selectedAgentId} idPrefix={`${instancePrefix}-login`} reference={otherTarget.databaseCredentialReference} onPendingChange={onLoginPendingChange} onStored={(reference) => changeOtherTarget({ ...otherTarget, databaseCredentialReference: reference })} />}
          </div>
        )}
      </div>
    </Section>
  )
}
