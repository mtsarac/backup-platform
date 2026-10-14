import { Section } from '@/components/ui/section'
import { FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'

interface SourceProps { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void }

export function SourceStep({ configuration, errors, onChange }: SourceProps) {
  const { text, number, check } = fieldKit(errors)
  return <Section title="MSSQL kaynak ve bağlantı" description="Yedeği alınacak SQL Server Instance ve Database.">
    <FormGrid>
      {text('configuration.host', 'SQL Server host / instance', configuration.host, host => onChange({ ...configuration, host }))}
      {text('configuration.database', 'Database', configuration.database, database => onChange({ ...configuration, database }))}
      {text('configuration.databaseCredentialReference', 'Kimlik bilgisi referansı (parola değil)', configuration.databaseCredentialReference ?? '', databaseCredentialReference => onChange({ ...configuration, databaseCredentialReference: databaseCredentialReference || null }), configuration.integratedSecurity)}
      {number('configuration.port', 'Port (isteğe bağlı)', configuration.port, port => onChange({ ...configuration, port }))}
      {check('Windows kimlik doğrulaması (integrated security)', configuration.integratedSecurity, integratedSecurity => onChange({ ...configuration, integratedSecurity }))}
      {check('Bağlantıyı şifrele', configuration.encrypt, encrypt => onChange({ ...configuration, encrypt }))}
      {check('Sunucu sertifikasına güven', configuration.trustServerCertificate, trustServerCertificate => onChange({ ...configuration, trustServerCertificate }))}
      <p className="text-sm text-text-muted md:col-span-2">Bu form parola kabul etmez. Kimlik bilgileri Agent yapılandırmasında tutulur; yalnızca referans adı gönderilir.</p>
    </FormGrid>
  </Section>
}

