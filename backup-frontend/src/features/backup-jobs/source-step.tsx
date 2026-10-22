import { tx, useLanguage } from '@/lib/i18n'
import { Section } from '@/components/ui/section'
import { FormGrid } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { DatabaseLoginFields } from '@/features/backup-jobs/database-login-fields'
import { DiscoverySourcePicker, type DiscoverySourceProps } from './discovery-source-picker'
import { FilePathList } from './file-path-list'

interface SourceProps { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void; discovery?: DiscoverySourceProps }

export function SourceStep({ configuration, errors, onChange, discovery }: SourceProps) {
  useLanguage()
  const { text, number, check, select } = fieldKit(errors)
  const isPg = configuration.engine === 'postgres'
  const isFiles = configuration.engine === 'file-system'
  const files = configuration.files ?? { paths: ['.'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' as const }
  return <Section title={isFiles ? tx('Dosya Kaynağı') : isPg ? tx("PostgreSQL kaynak ve bağlantı") : tx("MSSQL kaynak ve bağlantı")} description={isFiles ? tx('Agent üzerinde yapılandırılmış dosya kaynağını ve yedeklenecek göreli yolları seçin.') : isPg ? tx("Yedeği alınacak PostgreSQL sunucusu ve veritabanı.") : tx("Yedeği alınacak SQL Server Instance ve Database.")}>
    <div role="group" aria-label={tx('Engine')} className="mb-4 flex flex-wrap gap-4">
      <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
        <input type="radio" name="engine" value="mssql" checked={configuration.engine === 'mssql'} onChange={() => onChange({ ...configuration, engine: 'mssql', database: '', host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, databaseLogin: null, files: null, copyOnly: true, checksumPolicy: 'Required' })} className="size-4 accent-primary" />
        Microsoft SQL Server
      </label>
      <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
        <input type="radio" name="engine" value="postgres" checked={isPg} onChange={() => onChange({ ...configuration, engine: 'postgres', database: '', host: '', port: 5432, integratedSecurity: false, databaseCredentialReference: null, databaseLogin: null, files: null, copyOnly: false, checksumPolicy: 'Preferred' })} className="size-4 accent-primary" />
        PostgreSQL
      </label>
      <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
        <input type="radio" name="engine" value="file-system" checked={isFiles} onChange={() => onChange({ ...configuration, engine: 'file-system', database: '', host: 'localhost', port: null, integratedSecurity: true, databaseCredentialReference: null, databaseLogin: null, files, copyOnly: false, checksumPolicy: 'Required' })} className="size-4 accent-primary" />
        {tx('Dosya / Klasör')}
      </label>
    </div>
    <FormGrid>
      {isFiles ? <>
        {text('configuration.database', tx('Dosya kaynağı referansı'), configuration.database, database => onChange({ ...configuration, database }))}
        <p className="text-sm text-text-muted md:col-span-2">{tx('Referans Agent FileSources ayarındaki ada karşılık gelir; fiziksel kök yol sunucudan seçilemez. . tüm kaynağı seçer. İzinler ve SHA-256 manifesti arşivde korunur. Canlı dosya değişiklikleri yedeği durdurur; noktasal görünüm için yapılandırılmış snapshot kaynağı kullanın.')}</p>
        <FilePathList id="configuration.files.paths" label={tx('Yedeklenecek dosya ve klasörler')} paths={files.paths} error={errors['configuration.files.paths'] ?? errors['configuration.files']} onChange={paths => onChange({ ...configuration, files: { ...files, paths } })} />
        <FilePathList id="configuration.files.includes" label={tx('Dahil Edilecekler')} paths={files.includes} error={errors['configuration.files.includes']} onChange={includes => onChange({ ...configuration, files: { ...files, includes } })} />
        <FilePathList id="configuration.files.excludes" label={tx('Hariç Tutulacaklar')} paths={files.excludes} error={errors['configuration.files.excludes']} onChange={excludes => onChange({ ...configuration, files: { ...files, excludes } })} />
        {select('configuration.files.symlinkPolicy', tx('Sembolik Link Politikası'), files.symlinkPolicy, [['Skip', tx('Atla')], ['Preserve', tx('Koru')]], symlinkPolicy => onChange({ ...configuration, files: { ...files, symlinkPolicy: symlinkPolicy as 'Skip' | 'Preserve' } }))}
        <p className="text-sm text-text-muted md:col-span-2">{tx('Filtrelerde * tek yol bileşenini, ** alt klasörleri, ? tek karakteri eşleştirir. Boş dahil listesi tüm yolları kapsar; hariç kuralları önceliklidir. Korunan sembolik bağlantılar izlenmez ve kaynak dışına çıkamaz.')}</p>
      </> : <>
      {discovery && <DiscoverySourcePicker {...discovery} />}
      {text('configuration.host', isPg ? tx('PostgreSQL host') : tx('SQL Server host / instance'), configuration.host, host => onChange({ ...configuration, host }))}
      {text('configuration.database', 'Database', configuration.database, database => onChange({ ...configuration, database }), Boolean(discovery?.selected.length))}
      {!(!isPg && configuration.integratedSecurity) && <DatabaseLoginFields idPrefix="configuration-login" login={configuration.databaseLogin} hasStored={Boolean(configuration.databaseCredentialReference)}
        errors={{ userName: errors['configuration.databaseLogin.userName'], password: errors['configuration.databaseLogin.password'] }}
        onChange={databaseLogin => onChange({ ...configuration, databaseLogin })} />}
      {number('configuration.port', isPg ? tx('Port (varsayılan 5432)') : tx('Port (isteğe bağlı)'), configuration.port, port => onChange({ ...configuration, port }))}
      {!isPg && check(tx('Windows kimlik doğrulaması (integrated security)'), configuration.integratedSecurity, integratedSecurity => onChange({ ...configuration, integratedSecurity, databaseLogin: null, databaseCredentialReference: integratedSecurity ? null : configuration.databaseCredentialReference }))}
      {check(tx('Bağlantıyı şifrele'), configuration.encrypt, encrypt => onChange({ ...configuration, encrypt }))}
      {check(tx('Sunucu sertifikasına güven'), configuration.trustServerCertificate, trustServerCertificate => onChange({ ...configuration, trustServerCertificate }))}
      <p className="text-sm text-text-muted md:col-span-2">{tx("Parola şifrelenerek saklanır ve bir daha gösterilmez. Bu iş yalnızca burada girilen kullanıcıyla bağlanır; aynı makinedeki diğer SQL Server sürümlerinin ve kullanıcıların girişleri birbirine karışmaz.")}</p>
      {isPg && <p className="text-sm text-text-muted md:col-span-2">{tx("Full Backup, pg_dump custom-format mantıksal arşividir; WAL/PITR veya fiziksel küme yedeği değildir. Database adı en fazla 63 UTF-8 byte olabilir. PostgreSQL kullanıcı adı ve parolası gerekir.")}</p>}
      </>}
    </FormGrid>
  </Section>
}

