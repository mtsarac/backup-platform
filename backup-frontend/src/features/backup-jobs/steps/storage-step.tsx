import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { MultiDestinationPicker } from '@/features/storage-connections/multi-destination-picker'
import type { ComponentProps } from 'react'

type PickerProps = Omit<ComponentProps<typeof MultiDestinationPicker>, 'configuration' | 'fieldError' | 'onChange'>
interface Props { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void; picker: PickerProps }

/** Question: where the backup is written; key prefix and staging area stay under the technical details. */
export function StorageStep({ configuration, errors, onChange, picker }: Props) {
  useLanguage()
  const { text, withErrors } = fieldKit(errors)
  return <>
    <Question title={tx("Yedek nereye yazılsın?")} why={tx("Yedeğin güvenle saklanacağı yeri seçmeniz gerekir; seçmezseniz varsayılan depolama kullanılır.")}>
      <MultiDestinationPicker {...picker} configuration={configuration} fieldError={Object.entries(errors).find(([field]) => field.startsWith('configuration.destinations'))?.[1] ?? errors['configuration.requiredVerifiedReplicaCount']} onChange={onChange} />
    </Question>
    <Advanced open={withErrors('configuration.storagePrefix', 'configuration.stagingReference')}>
      {text('configuration.storagePrefix', tx('Hedef öneki (göreli anahtar)'), configuration.storagePrefix, storagePrefix => onChange({ ...configuration, storagePrefix }))}
      {text('configuration.stagingReference', tx('Geçici alan referansı (staging)'), configuration.stagingReference, stagingReference => onChange({ ...configuration, stagingReference }))}
    </Advanced>
  </>
}
