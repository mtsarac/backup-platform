import { tx, useLanguage } from '@/lib/i18n'
import { Section } from '@/components/ui/section'
import { Advanced } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { MultiDestinationPicker } from '@/features/storage-connections/multi-destination-picker'
import type { ComponentProps } from 'react'

type PickerProps = Omit<ComponentProps<typeof MultiDestinationPicker>, 'configuration' | 'fieldError' | 'onChange'>
interface Props { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void; picker: PickerProps }

/** Question group: where the backup is written. */
export function DestinationsGroup({ configuration, errors, onChange, picker }: Props) {
  useLanguage()
  return <Section title={tx("Depolama ve yürütme")} description={tx("Yedeğin yazılacağı hedefler.")}>
    <MultiDestinationPicker {...picker} configuration={configuration} fieldError={Object.entries(errors).find(([field]) => field.startsWith('configuration.destinations'))?.[1] ?? errors['configuration.requiredVerifiedReplicaCount']} onChange={onChange} />
  </Section>
}

/** Technical question group: key prefix and staging area. */
export function StorageDetailsGroup({ configuration, errors, onChange }: Omit<Props, 'picker'>) {
  useLanguage()
  const { text, withErrors } = fieldKit(errors)
  return <Advanced title={tx("Depolama ayrıntıları")} open={withErrors('configuration.storagePrefix', 'configuration.stagingReference')}>
    {text('configuration.storagePrefix', tx('Hedef öneki (göreli anahtar)'), configuration.storagePrefix, storagePrefix => onChange({ ...configuration, storagePrefix }))}
    {text('configuration.stagingReference', tx('Geçici alan referansı (staging)'), configuration.stagingReference, stagingReference => onChange({ ...configuration, stagingReference }))}
  </Advanced>
}

export function StorageStep({ picker, ...rest }: Props) {
  return <>
    <DestinationsGroup picker={picker} {...rest} />
    <StorageDetailsGroup {...rest} />
  </>
}
