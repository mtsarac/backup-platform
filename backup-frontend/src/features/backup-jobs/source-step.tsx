import { Section } from '@/components/ui/section'
import { FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'

interface SourceProps { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void }

export function SourceStep({ configuration, errors, onChange }: SourceProps) {
  const { text, number, check } = fieldKit(errors)
  const isPg = configuration.engine === 'postgres'
  return <Section title={isPg ? "PostgreSQL kaynak ve bağlantı" : "MSSQL kaynak ve bağlantı"} description={isPg ? "Yedeği alınacak PostgreSQL sunucusu ve veritabanı." : "Yedeği alınacak SQL Server Instance ve Database."}>
    <div className="mb-4 flex gap-4">
      <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
        <input type="radio" name="engine" value="mssql" checked={!isPg} onChange={() => onChange({ ...configuration, engine: 'mssql', port: null, integratedSecurity: true, databaseCredentialReference: null, copyOnly: true, checksumPolicy: 'Required' })} className="size-4 accent-primary" />
        Microsoft SQL Server
      </label>
      <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
        <input type="radio" name="engine" value="postgres" checked={isPg} onChange={() => onChange({ ...configuration, engine: 'postgres', port: 5432, integratedSecurity: false, databaseCredentialReference: null, copyOnly: false, checksumPolicy: 'Preferred' })} className="size-4 accent-primary" />
        PostgreSQL
      </label>
    </div>
    <FormGrid>
      {text('configuration.host', isPg ? 'PostgreSQL host' : 'SQL Server host / instance', configuration.host, host => onChange({ ...configuration, host }))}
      {text('configuration.database', 'Database', configuration.database, database => onChange({ ...configuration, database }))}
      {text('configuration.databaseCredentialReference', 'Kimlik bilgisi referansı (parola değil)', configuration.databaseCredentialReference ?? '', databaseCredentialReference => onChange({ ...configuration, databaseCredentialReference: databaseCredentialReference || null }), !isPg && configuration.integratedSecurity)}
      {number('configuration.port', isPg ? 'Port (varsayılan 5432)' : 'Port (isteğe bağlı)', configuration.port, port => onChange({ ...configuration, port }))}
      {!isPg && check('Windows kimlik doğrulaması (integrated security)', configuration.integratedSecurity, integratedSecurity => onChange({ ...configuration, integratedSecurity, databaseCredentialReference: integratedSecurity ? null : configuration.databaseCredentialReference }))}
      {check('Bağlantıyı şifrele', configuration.encrypt, encrypt => onChange({ ...configuration, encrypt }))}
      {check('Sunucu sertifikasına güven', configuration.trustServerCertificate, trustServerCertificate => onChange({ ...configuration, trustServerCertificate }))}
      <p className="text-sm text-text-muted md:col-span-2">Bu form parola kabul etmez. Kimlik bilgileri Agent yapılandırmasında tutulur; yalnızca referans adı gönderilir.</p>
      {isPg && <p className="text-sm text-text-muted md:col-span-2">Full Backup, pg_dump custom-format mantıksal arşividir; WAL/PITR veya fiziksel küme yedeği değildir. Database adı en fazla 63 UTF-8 byte olabilir. Agent üzerinde PostgreSQL kullanıcı adı/parola referansı gerekir.</p>}
    </FormGrid>
  </Section>
}

