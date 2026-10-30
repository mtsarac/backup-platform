import { tx, useLanguage } from '@/lib/i18n'
import { Advanced, FormGrid, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { DatabaseLoginFields } from '@/features/backup-jobs/database-login-fields'
import { DiscoverySourcePicker, type DiscoverySourceProps } from '@/features/backup-jobs/discovery-source-picker'
import { FilePathList } from '@/features/backup-jobs/file-path-list'
import { defaultFiles, type Engine } from '@/features/backup-jobs/job-form-values'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { isFeatureEnabled } from '@/features/feature-flags/feature-flags'
import { errorMessage } from '@/lib/api/failure-messages'
import { statusLabel } from '@/lib/status'

interface GroupProps { configuration: BackupConfiguration; errors: FieldErrors; onChange: (configuration: BackupConfiguration) => void }
const engines: readonly (readonly [Engine, string])[] = [['mssql', 'Microsoft SQL Server'], ['postgres', 'PostgreSQL'], ['file-system', 'Dosya / Klasör']]

/** Which kind of source is backed up. Each engine keeps the values typed for it while another is chosen. */
function EngineGroup({ engine, onEngine }: { engine: Engine; onEngine: (engine: Engine) => void }) {
  useLanguage()
  return <div role="group" aria-label={tx('Kaynak türü')} className="mb-4 flex flex-wrap gap-4">
    {engines.map(([value, label]) => <label key={value} className="flex items-center gap-2 cursor-pointer font-medium text-sm text-text-heading">
      <input type="radio" name="engine" value={value} checked={engine === value} onChange={() => onEngine(value)} className="size-4 accent-primary" />
      {value === 'file-system' ? tx('Dosya / Klasör') : label}
    </label>)}
  </div>
}

/** Which files and folders (file-system engine). */
function FileSourceGroup({ configuration, errors, onChange }: GroupProps) {
  useLanguage()
  const { text, select } = fieldKit(errors)
  const files = configuration.files ?? defaultFiles
  const snapshotsOn = isFeatureEnabled('windowsVss') || isFeatureEnabled('linuxSnapshots')
  return <FormGrid>
    {text('configuration.database', tx('Dosya kaynağı referansı'), configuration.database, database => onChange({ ...configuration, database }))}
    <p className="text-sm text-text-muted md:col-span-2">{tx('Referans Agent FileSources ayarındaki ada karşılık gelir; fiziksel kök yol sunucudan seçilemez. . tüm kaynağı seçer. İzinler ve SHA-256 manifesti arşivde korunur. Canlı dosya değişiklikleri yedeği durdurur.')}{snapshotsOn && ' ' + tx('Noktasal görünüm için yapılandırılmış snapshot kaynağı kullanın.')}</p>
    <FilePathList id="configuration.files.paths" label={tx('Yedeklenecek dosya ve klasörler')} paths={files.paths} error={errors['configuration.files.paths'] ?? errors['configuration.files']} onChange={paths => onChange({ ...configuration, files: { ...files, paths } })} />
    <FilePathList id="configuration.files.includes" label={tx('Dahil Edilecekler')} paths={files.includes} error={errors['configuration.files.includes']} onChange={includes => onChange({ ...configuration, files: { ...files, includes } })} />
    <FilePathList id="configuration.files.excludes" label={tx('Hariç Tutulacaklar')} paths={files.excludes} error={errors['configuration.files.excludes']} onChange={excludes => onChange({ ...configuration, files: { ...files, excludes } })} />
    {select('configuration.files.symlinkPolicy', tx('Sembolik Link Politikası'), files.symlinkPolicy, [['Skip', tx('Atla')], ['Preserve', tx('Koru')]], symlinkPolicy => onChange({ ...configuration, files: { ...files, symlinkPolicy: symlinkPolicy as 'Skip' | 'Preserve' } }))}
    <p className="text-sm text-text-muted md:col-span-2">{tx('Filtrelerde * tek yol bileşenini, ** alt klasörleri, ? tek karakteri eşleştirir. Boş dahil listesi tüm yolları kapsar; hariç kuralları önceliklidir. Korunan sembolik bağlantılar izlenmez ve kaynak dışına çıkamaz.')}</p>
  </FormGrid>
}

/** Which database server and how to connect (SQL Server / PostgreSQL); port and TLS settings live in the step's technical details. */
function DatabaseSourceGroup({ configuration, errors, onChange, discovery }: GroupProps & { discovery?: DiscoverySourceProps }) {
  useLanguage()
  const { text, check } = fieldKit(errors)
  const isPg = configuration.engine === 'postgres'
  return <FormGrid>
    {discovery && <DiscoverySourcePicker {...discovery} />}
    {text('configuration.host', isPg ? tx('PostgreSQL host') : tx('SQL Server host / instance'), configuration.host, host => onChange({ ...configuration, host }))}
    {text('configuration.database', 'Database', configuration.database, database => onChange({ ...configuration, database }), Boolean(discovery?.selected.length))}
    {!(!isPg && configuration.integratedSecurity) && <DatabaseLoginFields idPrefix="configuration-login" login={configuration.databaseLogin} hasStored={Boolean(configuration.databaseCredentialReference)}
      errors={{ userName: errors['configuration.databaseLogin.userName'], password: errors['configuration.databaseLogin.password'] }}
      onChange={databaseLogin => onChange({ ...configuration, databaseLogin })} />}
    {!isPg && check(tx('Windows kimlik doğrulaması (integrated security)'), configuration.integratedSecurity, integratedSecurity => onChange({ ...configuration, integratedSecurity, databaseLogin: null, databaseCredentialReference: integratedSecurity ? null : configuration.databaseCredentialReference }))}
    <p className="text-sm text-text-muted md:col-span-2">{isPg ? tx("Parola şifrelenerek saklanır ve bir daha gösterilmez. Bu iş yalnızca burada girilen kullanıcıyla bağlanır.") : tx("Parola şifrelenerek saklanır ve bir daha gösterilmez. Bu iş yalnızca burada girilen kullanıcıyla bağlanır; aynı makinedeki diğer SQL Server sürümlerinin ve kullanıcıların girişleri birbirine karışmaz.")}</p>
    {isPg && <p className="text-sm text-text-muted md:col-span-2">{tx("Full Backup, pg_dump custom-format mantıksal arşividir; WAL/PITR veya fiziksel küme yedeği değildir. Database adı en fazla 63 UTF-8 byte olabilir. PostgreSQL kullanıcı adı ve parolası gerekir.")}</p>}
  </FormGrid>
}

/** Questions: what the job is called, which computer runs it, and what is backed up. */
export function SourceStep({ form }: { form: JobFormState }) {
  useLanguage()
  const { values, errors, agents, set, setConfiguration, job } = form
  const { configuration } = values
  const { text, number, check, select, withErrors } = fieldKit(errors)
  const isFiles = configuration.engine === 'file-system'
  const isPg = configuration.engine === 'postgres'
  const discovery = job ? undefined : { agents: agents.data ?? [], agentId: values.agentId, selected: values.selected, onApply: form.applyDiscovery, onClear: form.clearDiscovery }
  const group = { configuration, errors, onChange: setConfiguration }
  return <>
    <Question title={tx("Yedekleme işi ve bilgisayar")} why={tx("İşi listelerde bu adla tanırsınız; yedeği, kaynağa ulaşabilen yedekleme bilgisayarındaki program alır.")}>
      <FormGrid>
        {text('name', tx('İş adı'), values.name, name => set({ name }))}
        <div className="min-w-0 space-y-1">
          {select('agentId', tx('Yedekleme bilgisayarı'), values.agentId, [['', tx('Yedekleme bilgisayarı seçin')], ...(agents.data ?? []).map(item => [item.agentId, `${item.hostName ?? tx('Adsız bilgisayar')} · ${statusLabel('agent', item.operationalStatus)}`] as const)], agentId => set({ agentId }))}
          {agents.isPending && <p role="status" className="text-sm text-text-muted">{tx("Bilgisayar listesi yükleniyor…")}</p>}{agents.error && <p role="alert" className="text-sm text-error">{errorMessage(agents.error)}</p>}
        </div>
      </FormGrid>
    </Question>
    <Question title={isFiles ? tx('Hangi dosya ve klasörleri yedekleyeceğiz?') : tx('Hangi veritabanını yedekleyeceğiz?')} why={tx('Yedeğin neyi kapsayacağını ve oraya nasıl ulaşılacağını bilmemiz gerekir.')}>
      <EngineGroup engine={configuration.engine} onEngine={form.changeEngine} />
      {isFiles ? <FileSourceGroup key={configuration.engine} {...group} /> : <DatabaseSourceGroup key={configuration.engine} {...group} discovery={discovery} />}
    </Question>
    <Advanced open={withErrors('enabled', 'configuration.port')}>
      {check(tx('İş etkin'), values.enabled, enabled => set({ enabled }))}
      {!isFiles && number('configuration.port', isPg ? tx('Port (varsayılan 5432)') : tx('Port (isteğe bağlı)'), configuration.port, port => setConfiguration({ ...configuration, port }))}
      {!isFiles && check(tx('Bağlantıyı şifrele'), configuration.encrypt, encrypt => setConfiguration({ ...configuration, encrypt }))}
      {!isFiles && check(tx('Sunucu sertifikasına güven'), configuration.trustServerCertificate, trustServerCertificate => setConfiguration({ ...configuration, trustServerCertificate }))}
    </Advanced>
  </>
}
