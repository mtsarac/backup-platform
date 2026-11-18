import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit } from '@/features/backup-jobs/job-field-kit'
import { encryptionPolicies, type EncryptionPolicy } from '@/features/artifact-encryption/types'
import { policyDescriptions, policyLabels } from '@/features/artifact-encryption/labels'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'

/** Question: whether backups are encrypted; copy-only, checksum and compression are pinned, not asked. */
export function ProtectionStep({ form, headingLevel }: { form: JobFormState; headingLevel?: 2 | 3 }) {
  useLanguage()
  const { errors, setConfiguration: onChange } = form
  const { configuration } = form.values
  const { select } = fieldKit(errors)
  return <>
    <Question compact={Boolean(form.job)} headingLevel={headingLevel} title={tx("Yedekler şifrelensin mi?")} why={tx("Şifreli bir yedek, dosya yanlış ellere geçse bile okunamaz.")}>
      <div className="min-w-0 space-y-1">
        {select('configuration.encryptionPolicy', tx('Yedek şifreleme politikası'), configuration.encryptionPolicy, encryptionPolicies.filter(policy => form.job || policy !== 'Off').map(policy => [policy, policyLabels[policy]] as const), value => onChange({ ...configuration, encryptionPolicy: value as EncryptionPolicy }))}
        <p className="text-sm text-text-muted">{policyDescriptions[configuration.encryptionPolicy]}</p>
        {configuration.encryptionPolicy === 'Required' && <p className="text-sm text-text-muted">{tx('Kurtarma için kendi anahtarınızı ekleyebilirsiniz.')} <Link className="text-primary underline" to={`/tenants/${form.tenantId}/settings/recovery-key`}>{tx('Kurtarma anahtarı')}</Link></p>}
      </div>
    </Question>
  </>
}
