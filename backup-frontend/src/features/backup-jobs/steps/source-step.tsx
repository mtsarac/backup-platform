import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, FormGrid, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { DatabaseLoginFields } from '@/features/backup-jobs/database-login-fields'
import { DiscoverySourcePicker, type DiscoverySourceProps } from '@/features/backup-jobs/discovery-source-picker'
import { FilePathList } from '@/features/backup-jobs/file-path-list'

interface GroupProps { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void }
const defaultFiles = { paths: ['.'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' as const }

/** Question group: which kind of source is backed up. */
export function EngineGroup({ configuration, onChange }: Pick<GroupProps, 'configuration' | 'onChange'>) {
  useLanguage()
  const isPg = configuration.engine === 'postgres'
  const isFiles = configuration.engine === 'file-system'
  const files = configuration.files ?? defaultFiles
  return <div role="group" aria-label={tx('Kaynak türü')} className="mb-4 flex flex-wrap gap-4">
    <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
      <input type="radio" name="engine" value="mssql" checked={configuration.engine === 'mssql'} onChange={() => onChange({ ...configuration, engine: 'mssql', database: '', host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, databaseLogin: null, files: null, copyOnly: true, checksumPolicy: 'Required' })} className="size-4 accent-primary" />
      Microsoft SQL Server
    </label>
    <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
      <input type="radio" name="engine" value="postgres" checked={isPg} onChange={() => onChange({ ...configuration, engine: 'postgres', database: '', host: '', port: 5432, integratedSecurity: false, databaseCredentialReference: null, databaseLogin: null, files: null, copyOnly: false, checksumPolicy: 'Preferred' })} className="size-4 accent-primary" />
      PostgreSQL
    </label>
    <label className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
      <input type="radio" name="engine" value="file-system" checked={isFiles} onChange={() => onChange({ ...configuration, engine: 'file-system', database: '', host: 'localhost', port: null, integratedSecurity: true, databaseCredentialReference: null, databaseLogin: null, files, copyOnly: true, checksumPolicy: 'Required' })} className="size-4 accent-primary" />
      {tx('Dosya / Klasör')}
    </label>
  </div>
}

/** Question group: which files and folders (file-system engine). */
export function FileSourceGroup({ configuration, errors, onChange }: GroupProps) {
  useLanguage()
  const { text, select } = fieldKit(errors)
  const files = configuration.files ?? defaultFiles
  return <FormGrid>
    {text('configuration.database', tx('Dosya kaynağı referansı'), configuration.database, database => onChange({ ...configuration, database }))}
    <p className="text-sm text-text-muted md:col-span-2">{tx('Referans Agent FileSources ayarındaki ada karşılık gelir; fiziksel kök yol sunucudan seçilemez. . tüm kaynağı seçer. İzinler ve SHA-256 manifesti arşivde korunur. Canlı dosya değişiklikleri yedeği durdurur; noktasal görünüm için yapılandırılmış snapshot kaynağı kullanın.')}</p>
    <FilePathList id="configuration.files.paths" label={tx('Yedeklenecek dosya ve klasörler')} paths={files.paths} error={errors['configuration.files.paths'] ?? errors['configuration.files']} onChange={paths => onChange({ ...configuration, files: { ...files, paths } })} />
    <FilePathList id="configuration.files.includes" label={tx('Dahil Edilecekler')} paths={files.includes} error={errors['configuration.files.includes']} onChange={includes => onChange({ ...configuration, files: { ...files, includes } })} />
    <FilePathList id="configuration.files.excludes" label={tx('Hariç Tutulacaklar')} paths={files.excludes} error={errors['configuration.files.excludes']} onChange={excludes => onChange({ ...configuration, files: { ...files, excludes } })} />
    {select('configuration.files.symlinkPolicy', tx('Sembolik Link Politikası'), files.symlinkPolicy, [['Skip', tx('Atla')], ['Preserve', tx('Koru')]], symlinkPolicy => onChange({ ...configuration, files: { ...files, symlinkPolicy: symlinkPolicy as 'Skip' | 'Preserve' } }))}
    <p className="text-sm text-text-muted md:col-span-2">{tx('Filtrelerde * tek yol bileşenini, ** alt klasörleri, ? tek karakteri eşleştirir. Boş dahil listesi tüm yolları kapsar; hariç kuralları önceliklidir. Korunan sembolik bağlantılar izlenmez ve kaynak dışına çıkamaz.')}</p>
  </FormGrid>
}

/** Question group: which database server and how to connect (SQL Server / PostgreSQL). */
export function DatabaseSourceGroup({ configuration, errors, onChange, discovery }: GroupProps & { discovery?: DiscoverySourceProps }) {
  useLanguage()
  const { text, number, check, withErrors } = fieldKit(errors)
  const isPg = configuration.engine === 'postgres'
  return <>
    <FormGrid>
    {discovery && <DiscoverySourcePicker {...discovery} />}
    {text('configuration.host', isPg ? tx('PostgreSQL host') : tx('SQL Server host / instance'), configuration.host, host => onChange({ ...configuration, host }))}
    {text('configuration.database', 'Database', configuration.database, database => onChange({ ...configuration, database }), Boolean(discovery?.selected.length))}
    {!(!isPg && configuration.integratedSecurity) && <DatabaseLoginFields idPrefix="configuration-login" login={configuration.databaseLogin} hasStored={Boolean(configuration.databaseCredentialReference)}
      errors={{ userName: errors['configuration.databaseLogin.userName'], password: errors['configuration.databaseLogin.password'] }}
      onChange={databaseLogin => onChange({ ...configuration, databaseLogin })} />}
    {!isPg && check(tx('Windows kimlik doğrulaması (integrated security)'), configuration.integratedSecurity, integratedSecurity => onChange({ ...configuration, integratedSecurity, databaseLogin: null, databaseCredentialReference: integratedSecurity ? null : configuration.databaseCredentialReference }))}
    <p className="text-sm text-text-muted md:col-span-2">{tx("Parola şifrelenerek saklanır ve bir daha gösterilmez. Bu iş yalnızca burada girilen kullanıcıyla bağlanır; aynı makinedeki diğer SQL Server sürümlerinin ve kullanıcıların girişleri birbirine karışmaz.")}</p>
    {isPg && <p className="text-sm text-text-muted md:col-span-2">{tx("Full Backup, pg_dump custom-format mantıksal arşividir; WAL/PITR veya fiziksel küme yedeği değildir. Database adı en fazla 63 UTF-8 byte olabilir. PostgreSQL kullanıcı adı ve parolası gerekir.")}</p>}
    </FormGrid>
    <Advanced open={withErrors('configuration.port')}>
      {number('configuration.port', isPg ? tx('Port (varsayılan 5432)') : tx('Port (isteğe bağlı)'), configuration.port, port => onChange({ ...configuration, port }))}
      {check(tx('Bağlantıyı şifrele'), configuration.encrypt, encrypt => onChange({ ...configuration, encrypt }))}
      {check(tx('Sunucu sertifikasına güven'), configuration.trustServerCertificate, trustServerCertificate => onChange({ ...configuration, trustServerCertificate }))}
    </Advanced>
  </>
}

export function SourceStep({ discovery, ...group }: GroupProps & { discovery?: DiscoverySourceProps }) {
  useLanguage()
  const isFiles = group.configuration.engine === 'file-system'
  return <Question title={isFiles ? tx('Hangi dosya ve klasörleri yedekleyeceğiz?') : tx('Hangi veritabanını yedekleyeceğiz?')} why={tx('Yedeğin neyi kapsayacağını ve oraya nasıl ulaşılacağını bilmemiz gerekir.')}>
    <EngineGroup configuration={group.configuration} onChange={group.onChange} />
    {isFiles ? <FileSourceGroup {...group} /> : <DatabaseSourceGroup {...group} discovery={discovery} />}
  </Question>
}
