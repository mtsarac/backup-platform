import { tx, useLanguage } from '@/lib/i18n'
import { Card } from '@/components/ui/card'
import { ConfigFieldInputs, SecretFieldInputs } from './descriptor-fields'
import { activeSecretFields } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import { invalidFieldName } from './labels'
import type { ProviderDescriptor } from './types'

/** Safe descriptor configuration followed by the selected authentication method's new credentials. */
export function ProviderFields({ provider, config, secret, error, onConfig, onSecret }: {
  provider: ProviderDescriptor; config: FieldValues; secret: Record<string, string>; error: unknown
  onConfig: (name: string, value: string | boolean) => void; onSecret: (name: string, value: string) => void
}) {
  useLanguage()
  const fields = activeSecretFields(provider.providerType, config, provider.secretFields)
  return <>
    <ConfigFieldInputs providerType={provider.providerType} fields={provider.configFields} values={config} invalidName={invalidFieldName(error, 'configuration')} onChange={onConfig} />
    {fields.length > 0 && <Card tone="subtle" className="space-y-3">
      <p className="text-sm text-text-muted">{tx("Kimlik bilgisi değerleri yalnızca sunucuya gönderilir; kaydedildikten sonra bir daha gösterilmez.")}</p>
      <SecretFieldInputs providerType={provider.providerType} fields={fields} values={secret} invalidName={invalidFieldName(error, 'secret')} onChange={onSecret} />
    </Card>}
  </>
}

