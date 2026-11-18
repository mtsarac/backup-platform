import { tx, useLanguage } from '@/lib/i18n'
import { FormGrid, Question } from '@/features/backup-jobs/job-form-fields'
import { MultiDestinationPicker } from '@/features/storage-connections/multi-destination-picker'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'

/** Question: where the backup is written; key prefix, staging area and execution location are pinned, not asked. */
export function StorageStep({ form }: { form: JobFormState }) {
  useLanguage()
  const { errors, setConfiguration, picker } = form
  const { configuration } = form.values
  return <>
    <Question compact={Boolean(form.job)} title={tx("Yedek nereye yazılsın?")} why={tx("Yedeğin güvenle saklanacağı yeri seçmeniz gerekir; seçmezseniz varsayılan depolama kullanılır.")}>
      <FormGrid><MultiDestinationPicker {...picker} compact={Boolean(form.job)} configuration={configuration} fieldError={Object.entries(errors).find(([field]) => field.startsWith('configuration.destinations'))?.[1]} replicaError={errors['configuration.requiredVerifiedReplicaCount']} replicationError={errors['configuration.sourceSite'] || errors['configuration.enforceThreeTwoOne']} onChange={setConfiguration} /></FormGrid>
    </Question>
  </>
}
