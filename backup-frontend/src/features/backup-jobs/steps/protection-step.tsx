import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { encryptionPolicies, type EncryptionPolicy } from '@/features/artifact-encryption/types'
import { policyDescriptions, policyLabels } from '@/features/artifact-encryption/labels'

const policies = [['Disabled', 'Off — kapalı'], ['Preferred', 'IfSupported — destekleniyorsa'], ['Required', 'Required — zorunlu']] as const

interface Props { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void }

/** Question: whether backups are encrypted. Copy-only, checksum and compression stay under the technical details. */
export function ProtectionStep({ configuration, errors, onChange }: Props) {
  useLanguage()
  const { check, select, withErrors } = fieldKit(errors)
  return <>
    <Question title={tx("Yedekler şifrelensin mi?")} why={tx("Şifreli bir yedek, dosya yanlış ellere geçse bile okunamaz.")}>
      <div className="min-w-0 space-y-1">
        {select('configuration.encryptionPolicy', tx('Yedek şifreleme politikası'), configuration.encryptionPolicy, encryptionPolicies.map(policy => [policy, policyLabels[policy]] as const), value => onChange({ ...configuration, encryptionPolicy: value as EncryptionPolicy }))}
        <p className="text-sm text-text-muted">{policyDescriptions[configuration.encryptionPolicy]}{' '}{tx("Şifreleme Agent'ta, depolamaya yazılmadan önce yapılır; yalnızca sonraki çalıştırmalar etkilenir, mevcut yedekler değişmez.")}</p>
      </div>
    </Question>
    <Advanced open={withErrors('configuration.copyOnly', 'configuration.checksumPolicy', 'configuration.compressionPolicy')}>
      {configuration.engine === 'mssql' && check(tx('Kopya yedek (COPY_ONLY: mevcut yedek zincirini etkilemez)'), configuration.copyOnly, copyOnly => onChange({ ...configuration, copyOnly }))}
      {configuration.engine !== 'file-system' && select('configuration.checksumPolicy', configuration.engine === 'postgres' ? tx('Sayfa bütünlüğü (data_checksums)') : tx('Bütünlük doğrulaması (CHECKSUM)'), configuration.checksumPolicy, policies, value => onChange({ ...configuration, checksumPolicy: value as BackupConfiguration['checksumPolicy'] }))}
      {select('configuration.compressionPolicy', tx('Sıkıştırma (Compression)'), configuration.compressionPolicy, policies, value => onChange({ ...configuration, compressionPolicy: value as BackupConfiguration['compressionPolicy'] }))}
      {configuration.engine === 'postgres' && <p className="text-sm text-text-muted md:col-span-2">{tx("pg_dump custom arşivi mevcut yedek zincirini değiştirmez; COPY_ONLY uygulanmaz. data_checksums sunucunun sayfa doğrulamasıdır: Required yalnızca etkin kümelerde çalışır, Preferred desteklenmiyorsa uyarır. Arşiv ve replikalar her durumda SHA-256 ile doğrulanır. Sıkıştırma pg_dump arşiv sıkıştırmasıdır.")}</p>}
      {configuration.engine === 'file-system' && <p className="text-sm text-text-muted md:col-span-2">{tx('Dosya içeriği ve manifest SHA-256 ile her zaman doğrulanır. Dosya izinleri ve zaman damgaları geri yüklenir; kopya yedek ve veritabanı sayfa doğrulaması uygulanmaz.')}</p>}
    </Advanced>
  </>
}
