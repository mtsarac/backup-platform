import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit } from '@/features/backup-jobs/job-field-kit'
import { MultiDestinationPicker } from '@/features/storage-connections/multi-destination-picker'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'

/** Question: where the backup is written; key prefix and staging area stay under the technical details. */
export function StorageStep({ form }: { form: JobFormState }) {
  useLanguage()
  const { errors, setConfiguration, picker } = form
  const { configuration } = form.values
  const { text, withErrors } = fieldKit(errors)
  return <>
    <Question title={tx("Yedek nereye yazılsın?")} why={tx("Yedeğin güvenle saklanacağı yeri seçmeniz gerekir; seçmezseniz varsayılan depolama kullanılır.")}>
      <MultiDestinationPicker {...picker} configuration={configuration} fieldError={Object.entries(errors).find(([field]) => field.startsWith('configuration.destinations'))?.[1] ?? errors['configuration.requiredVerifiedReplicaCount']} onChange={setConfiguration} />
    </Question>
    <Advanced open={withErrors('configuration.storagePrefix', 'configuration.stagingReference')}>
      {text('configuration.storagePrefix', tx('Hedef öneki (göreli anahtar)'), configuration.storagePrefix, storagePrefix => setConfiguration({ ...configuration, storagePrefix }))}
      {text('configuration.stagingReference', tx('Geçici alan referansı (staging)'), configuration.stagingReference, stagingReference => setConfiguration({ ...configuration, stagingReference }))}
    </Advanced>
  </>
}
