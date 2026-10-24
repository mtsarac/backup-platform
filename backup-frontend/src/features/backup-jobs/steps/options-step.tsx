import { tx, useLanguage } from '@/lib/i18n'
import { Section } from '@/components/ui/section'
import { FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration, RetentionPolicy } from '@/features/backup-jobs/types'
import { encryptionPolicies, type EncryptionPolicy } from '@/features/artifact-encryption/types'
import { policyDescriptions, policyLabels } from '@/features/artifact-encryption/labels'

const policies = [['Disabled', 'Off — kapalı'], ['Preferred', 'IfSupported — destekleniyorsa'], ['Required', 'Required — zorunlu']] as const

interface RetentionProps { errors: FieldErrors; retention: RetentionPolicy; onRetention: (retention: RetentionPolicy) => void; canAdmin: boolean; existing: boolean }

/** Question group: how long backups are kept. */
export function RetentionGroup({ errors, retention, onRetention, canAdmin, existing }: RetentionProps) {
  useLanguage()
  const { number, check } = fieldKit(errors)
  return <Section title={tx("Saklama politikası")} description={tx("Yedeklerin ne kadar süre saklanacağı.")}>
    <FormGrid>
      {!canAdmin && <p className="text-sm text-text-muted md:col-span-2">{tx("Saklama politikasını yalnızca Owner / Admin değiştirebilir.")}{' '}{existing ? tx('Mevcut politika korunur.') : tx('Sunucunun varsayılan politikası uygulanır.')}</p>}
      {check(tx('Saklama süresi etkin'), retention.enabled, value => onRetention({ ...retention, enabled: value }), !canAdmin)}
      {number('retention.keepForSeconds', tx('KeepFor — saklama süresi (saniye)'), retention.keepForSeconds, value => onRetention({ ...retention, keepForSeconds: value ?? 0 }), !canAdmin)}
      {number('retention.deleteGracePeriodSeconds', tx('DeleteGracePeriod — silme bekleme süresi (saniye)'), retention.deleteGracePeriodSeconds, value => onRetention({ ...retention, deleteGracePeriodSeconds: value ?? 0 }), !canAdmin)}
      <p className="text-sm text-text-muted md:col-span-2">{tx("Politika değişikliği mevcut yedeklerin son kullanma tarihini değiştirmez.")}</p>
    </FormGrid>
  </Section>
}

interface OptionsProps { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void }

/** Question group: copy-only, checksum, compression and encryption of the backup. */
export function BackupOptionsGroup({ configuration, errors, onChange }: OptionsProps) {
  useLanguage()
  const { check, select } = fieldKit(errors)
  return <Section title={tx("Yedekleme seçenekleri")}>
    <FormGrid>
      {configuration.engine === 'mssql' && check(tx('Kopya yedek (COPY_ONLY: mevcut yedek zincirini etkilemez)'), configuration.copyOnly, copyOnly => onChange({ ...configuration, copyOnly }))}
      {configuration.engine !== 'file-system' && select('configuration.checksumPolicy', configuration.engine === 'postgres' ? tx('Sayfa bütünlüğü (data_checksums)') : tx('Bütünlük doğrulaması (CHECKSUM)'), configuration.checksumPolicy, policies, value => onChange({ ...configuration, checksumPolicy: value as BackupConfiguration['checksumPolicy'] }))}
      {select('configuration.compressionPolicy', tx('Sıkıştırma (Compression)'), configuration.compressionPolicy, policies, value => onChange({ ...configuration, compressionPolicy: value as BackupConfiguration['compressionPolicy'] }))}
      <div className="min-w-0 space-y-1 md:col-span-2">
        {select('configuration.encryptionPolicy', tx('Yedek şifreleme politikası'), configuration.encryptionPolicy, encryptionPolicies.map(policy => [policy, policyLabels[policy]] as const), value => onChange({ ...configuration, encryptionPolicy: value as EncryptionPolicy }))}
        <p className="text-sm text-text-muted">{policyDescriptions[configuration.encryptionPolicy]}{' '}{tx("Şifreleme Agent'ta, depolamaya yazılmadan önce yapılır; yalnızca sonraki çalıştırmalar etkilenir, mevcut yedekler değişmez.")}</p>
      </div>
      {configuration.engine === 'postgres' && <p className="text-sm text-text-muted md:col-span-2">{tx("pg_dump custom arşivi mevcut yedek zincirini değiştirmez; COPY_ONLY uygulanmaz. data_checksums sunucunun sayfa doğrulamasıdır: Required yalnızca etkin kümelerde çalışır, Preferred desteklenmiyorsa uyarır. Arşiv ve replikalar her durumda SHA-256 ile doğrulanır. Sıkıştırma pg_dump arşiv sıkıştırmasıdır.")}</p>}
      {configuration.engine === 'file-system' && <p className="text-sm text-text-muted md:col-span-2">{tx('Dosya içeriği ve manifest SHA-256 ile her zaman doğrulanır. Dosya izinleri ve zaman damgaları geri yüklenir; kopya yedek ve veritabanı sayfa doğrulaması uygulanmaz.')}</p>}
    </FormGrid>
  </Section>
}

export function OptionsStep({ retention, onRetention, canAdmin, existing, ...options }: Omit<RetentionProps, 'errors'> & OptionsProps) {
  return <>
    <RetentionGroup errors={options.errors} retention={retention} onRetention={onRetention} canAdmin={canAdmin} existing={existing} />
    <BackupOptionsGroup {...options} />
  </>
}
