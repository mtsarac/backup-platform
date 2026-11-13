import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, FormGrid, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit } from '@/features/backup-jobs/job-field-kit'
import { MultiDestinationPicker } from '@/features/storage-connections/multi-destination-picker'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { primaryDestination } from '@/features/storage-connections/job-connection-picker-utils'
import { executionLocationLabel } from '@/features/storage-connections/labels'

/** Question: where the backup is written; key prefix and staging area stay under the technical details. */
export function StorageStep({ form }: { form: JobFormState }) {
  useLanguage()
  const { errors, setConfiguration, picker } = form
  const { configuration } = form.values
  const { text, withErrors } = fieldKit(errors)
  return <>
    <Question compact={Boolean(form.job)} title={tx("Yedek nereye yazılsın?")} why={tx("Yedeğin güvenle saklanacağı yeri seçmeniz gerekir; seçmezseniz varsayılan depolama kullanılır.")}>
      <FormGrid><MultiDestinationPicker {...picker} compact={Boolean(form.job)} configuration={configuration} fieldError={Object.entries(errors).find(([field]) => field.startsWith('configuration.destinations'))?.[1]} replicaError={errors['configuration.requiredVerifiedReplicaCount']} replicationError={errors['configuration.sourceSite'] || errors['configuration.enforceThreeTwoOne']} onChange={setConfiguration} /></FormGrid>
    </Question>
    <Advanced open={withErrors('configuration.storagePrefix', 'configuration.stagingReference')}>
      {form.job && <div className="space-y-1"><span className="block text-sm font-medium text-text-heading">{tx("Depolama yürütme konumu")}</span><p className="text-sm">{executionLocationLabel(primaryDestination(configuration).storageExecutionLocation)}</p><p className="text-xs text-text-muted">{tx("Yeni hedef seçildiğinde sağlayıcının yürütme konumu uygulanır.")}</p></div>}
      {text('configuration.storagePrefix', tx('Hedef öneki (göreli anahtar)'), configuration.storagePrefix, storagePrefix => setConfiguration({ ...configuration, storagePrefix }))}
      {text('configuration.stagingReference', tx('Geçici alan referansı (staging)'), configuration.stagingReference, stagingReference => setConfiguration({ ...configuration, stagingReference }))}
    </Advanced>
  </>
}
