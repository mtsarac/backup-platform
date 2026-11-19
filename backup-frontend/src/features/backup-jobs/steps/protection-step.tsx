import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Question } from '@/features/backup-jobs/job-form-fields'
import { EncryptionSwitch } from '@/features/artifact-encryption/encryption-switch'
import { encryptionOn } from '@/features/artifact-encryption/labels'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'

/** Question: whether backups are encrypted; copy-only, checksum and compression are pinned, not asked. */
export function ProtectionStep({ form, headingLevel }: { form: JobFormState; headingLevel?: 2 | 3 }) {
  useLanguage()
  const { setConfiguration: onChange } = form
  const { configuration } = form.values
  return <>
    <Question compact={Boolean(form.job)} headingLevel={headingLevel} title={tx("Yedekler şifrelensin mi?")} why={tx("Şifreli bir yedek, dosya yanlış ellere geçse bile okunamaz.")}>
      <div className="min-w-0 space-y-1">
        <EncryptionSwitch policy={configuration.encryptionPolicy} onChange={encryptionPolicy => onChange({ ...configuration, encryptionPolicy })} />
        {encryptionOn(configuration.encryptionPolicy) && <p className="text-sm text-text-muted">{tx('Kurtarma için kendi anahtarınızı ekleyebilirsiniz.')} <Link className="text-primary underline" to={`/tenants/${form.tenantId}/settings/recovery-key`}>{tx('Kurtarma anahtarı')}</Link></p>}
      </div>
    </Question>
  </>
}
