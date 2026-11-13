import { tx, useLanguage } from '@/lib/i18n'
import { RadioField } from '@/components/ui/radio-field'
import { Disclosure } from '@/components/ui/disclosure'
import { Advanced, FormGrid, Question } from '@/features/backup-jobs/job-form-fields'
import { fieldKit, type FieldErrors } from '@/features/backup-jobs/job-field-kit'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { DatabaseLoginFields } from '@/features/backup-jobs/database-login-fields'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ErrorNotice } from '@/components/ui/notice'
import { DiscoverySourcePicker } from '@/features/backup-jobs/discovery-source-picker'
import { engineLabels } from '@/features/discovery/status'
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
    {engines.map(([value, label]) => <RadioField key={value} className="font-medium text-text-heading" name="engine" value={value} checked={engine === value} onChange={() => onEngine(value)} label={value === 'file-system' ? tx('Dosya / Klasör') : label} />)}
  </div>
}

/** Which files and folders (file-system engine). */
function FileSourceGroup({ configuration, errors, onChange, edit }: GroupProps & { edit: boolean }) {
  useLanguage()
  const { text, select } = fieldKit(errors)
  const files = configuration.files ?? defaultFiles
  const snapshotsOn = isFeatureEnabled('windowsVss') || isFeatureEnabled('linuxSnapshots')
  const pathError = (field: string) => Object.entries(errors).find(([key]) => key === field || key.startsWith(`${field}[`) || key.startsWith(`${field}.`))?.[1]
  const fileSourceHelp = <p className="text-sm text-text-muted md:col-span-2">{tx('Referans Agent FileSources ayarındaki ada karşılık gelir; fiziksel kök yol sunucudan seçilemez. . tüm kaynağı seçer. İzinler ve SHA-256 manifesti arşivde korunur. Canlı dosya değişiklikleri yedeği durdurur.')}{snapshotsOn && ' ' + tx('Noktasal görünüm için yapılandırılmış snapshot kaynağı kullanın.')}</p>
  const filters = <>
    <FilePathList id="configuration.files.includes" label={tx('Dahil Edilecekler')} paths={files.includes} error={pathError('configuration.files.includes')} onChange={includes => onChange({ ...configuration, files: { ...files, includes } })} />
    <FilePathList id="configuration.files.excludes" label={tx('Hariç Tutulacaklar')} paths={files.excludes} error={pathError('configuration.files.excludes')} onChange={excludes => onChange({ ...configuration, files: { ...files, excludes } })} />
    {select('configuration.files.symlinkPolicy', tx('Sembolik Link Politikası'), files.symlinkPolicy, [['Skip', tx('Atla')], ['Preserve', tx('Koru')]], symlinkPolicy => onChange({ ...configuration, files: { ...files, symlinkPolicy: symlinkPolicy as 'Skip' | 'Preserve' } }))}
    <p className="text-sm text-text-muted md:col-span-2">{tx('Filtrelerde * tek yol bileşenini, ** alt klasörleri, ? tek karakteri eşleştirir. Boş dahil listesi tüm yolları kapsar; hariç kuralları önceliklidir. Korunan sembolik bağlantılar izlenmez ve kaynak dışına çıkamaz.')}</p>
  </>
  return <FormGrid>
    {text('configuration.database', edit ? tx('Dosya kaynağı adı') : tx('Dosya kaynağı referansı'), configuration.database, database => onChange({ ...configuration, database }))}
    {edit ? <p className="text-sm text-text-muted md:col-span-2">{tx('Bu ad, yedekleme bilgisayarında yapılandırılmış dosya kaynağıdır; fiziksel kök yol sunucudan seçilemez. Tüm kaynağı seçmek için . kullanın.')}</p> : fileSourceHelp}
    <FilePathList id="configuration.files.paths" label={tx('Yedeklenecek dosya ve klasörler')} paths={files.paths} error={pathError('configuration.files.paths') ?? errors['configuration.files']} onChange={paths => onChange({ ...configuration, files: { ...files, paths } })} />
    {edit ? <div className="md:col-span-2"><Disclosure title={tx('Dosya filtreleri')} open={Boolean(pathError('configuration.files.includes') || pathError('configuration.files.excludes') || errors['configuration.files.symlinkPolicy'])}><FormGrid>{fileSourceHelp}{filters}</FormGrid></Disclosure></div> : filters}
  </FormGrid>
}

/** Which database server and how to connect (SQL Server / PostgreSQL); port and TLS settings live in the step's technical details. */
function DatabaseSourceGroup({ configuration, errors, onChange }: GroupProps) {
  useLanguage()
  const { text, check } = fieldKit(errors)
  const isPg = configuration.engine === 'postgres'
  return <FormGrid>
    {text('configuration.host', isPg ? tx('PostgreSQL host') : tx('SQL Server host / instance'), configuration.host, host => onChange({ ...configuration, host }))}
    {text('configuration.database', 'Database', configuration.database, database => onChange({ ...configuration, database }))}
    {!(!isPg && configuration.integratedSecurity) && <DatabaseLoginFields idPrefix="configuration-login" login={configuration.databaseLogin} hasStored={Boolean(configuration.databaseCredentialReference)}
      errors={{ userName: errors['configuration.databaseLogin.userName'], password: errors['configuration.databaseLogin.password'] }}
      onChange={databaseLogin => onChange({ ...configuration, databaseLogin })} />}
    {!isPg && check(tx('Windows kimlik doğrulaması (integrated security)'), configuration.integratedSecurity, integratedSecurity => onChange({ ...configuration, integratedSecurity, databaseLogin: null, databaseCredentialReference: integratedSecurity ? null : configuration.databaseCredentialReference }))}
    <p className="text-sm text-text-muted md:col-span-2">{isPg ? tx("Parola şifrelenerek saklanır ve bir daha gösterilmez. Bu iş yalnızca burada girilen kullanıcıyla bağlanır.") : tx("Parola şifrelenerek saklanır ve bir daha gösterilmez. Bu iş yalnızca burada girilen kullanıcıyla bağlanır; aynı makinedeki diğer SQL Server sürümlerinin ve kullanıcıların girişleri birbirine karışmaz.")}</p>
    {isPg && <p className="text-sm text-text-muted md:col-span-2">{tx("Full Backup, pg_dump custom-format mantıksal arşividir; WAL/PITR veya fiziksel küme yedeği değildir. Database adı en fazla 63 UTF-8 byte olabilir. PostgreSQL kullanıcı adı ve parolası gerekir.")}</p>}
  </FormGrid>
}

const sourceErrorKeys = ['source', 'configuration.host', 'configuration.database', 'configuration.port', 'configuration.databaseLogin.userName', 'configuration.databaseLogin.password']

/** A source chosen from the inventory, shown read-only: the computer is part of it and is never asked again. */
function DiscoveredSource({ form }: { form: JobFormState }) {
  useLanguage()
  const { values, agents, discovered } = form
  if (!discovered) return null
  const computer = agents.isPending ? tx('Yükleniyor…') : agents.data?.find(item => item.agentId === discovered.agentId)?.hostName ?? tx('Adsız bilgisayar')
  const rows: readonly (readonly [string, string])[] = [
    [tx('Yedekleme bilgisayarı'), computer],
    [tx('Veritabanı sunucusu'), `${discovered.displayName} · ${engineLabels[discovered.engine]} · ${discovered.host}:${discovered.port}`],
    [tx('Veritabanı'), values.selected.join(', ')],
  ]
  return <Card tone="subtle" className="space-y-3">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="font-medium text-text-heading">{tx("Seçilen kaynak")}</h3>
      <Button type="button" variant="outline" size="sm" onClick={form.clearDiscovery}>{tx("Değiştir")}</Button>
    </div>
    <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[max-content_1fr]">
      {rows.map(([term, detail]) => <div key={term} className="contents"><dt className="text-text-muted">{term}</dt><dd className="min-w-0 break-words text-text">{detail}</dd></div>)}
    </dl>
    {values.selected.length > 1 && <p className="text-sm text-text-muted">{tx("Her veritabanı için ayrı bir yedekleme işi oluşturulur.")}</p>}
  </Card>
}

/** Primary way to start: pick computer, instance and databases from what discovery found. Typing the source by hand is the quieter alternative. */
function DiscoverySourceChoice({ form }: { form: JobFormState }) {
  useLanguage()
  const { agents, values } = form
  const manual = () => form.set({ sourceMode: 'manual' })
  if (agents.isPending) return <p role="status" className="text-sm text-text-muted">{tx("Bilgisayar listesi yükleniyor…")}</p>
  if (agents.error) return <ErrorNotice title={tx("Bilgisayar listesi alınamadı")} error={agents.error} onRetry={() => void agents.refetch()} />
  return <div className="space-y-4">
    <Card className="space-y-3 border-primary/40">
      <div>
        <h3 className="font-medium text-text-heading">{tx("Otomatik keşiften seç")} <span className="text-sm font-normal text-text-muted">{tx("(önerilen)")}</span></h3>
        <p className="text-sm text-text-muted">{tx("Bilgisayar, veritabanı sunucusu ve veritabanı, bulunanlar arasından seçilir; bağlantı bilgileri kendiliğinden gelir.")}</p>
      </div>
      <DiscoverySourcePicker agents={agents.data ?? []} agentId={values.agentId} instanceId={form.prefillInstanceId} onApply={form.applyDiscovery} />
    </Card>
    <div className="flex flex-wrap items-center gap-3">
      <Button type="button" variant="outline" onClick={manual}>{tx("Elle yapılandır")}</Button>
      <p className="min-w-0 flex-1 text-sm text-text-muted">{tx("Dosya / klasör yedekleri veya listede olmayan bir veritabanı için kaynağı kendiniz girersiniz.")}</p>
    </div>
  </div>
}

/** Questions: what the job is called, which computer runs it, and what is backed up. */
export function SourceStep({ form }: { form: JobFormState }) {
  useLanguage()
  const { values, errors, agents, set, setConfiguration, job } = form
  const { configuration } = values
  const { text, number, check, select, withErrors } = fieldKit(errors)
  const isFiles = configuration.engine === 'file-system'
  const isPg = configuration.engine === 'postgres'
  // Existing jobs are edited by hand; a new job starts from discovery and falls back to typing.
  const manual = Boolean(job) || values.sourceMode === 'manual'
  const picked = Boolean(form.discovered) && values.selected.length > 0
  const sourceError = sourceErrorKeys.map(key => errors[key]).find(Boolean)
  const group = { configuration, errors, onChange: setConfiguration }
  return <>
    <Question compact={Boolean(job)} title={manual ? tx("Yedekleme işi ve bilgisayar") : tx("Yedekleme işi")} why={manual ? tx("İşi listelerde bu adla tanırsınız; yedeği, kaynağa ulaşabilen yedekleme bilgisayarındaki program alır.") : tx("İşi listelerde bu adla tanırsınız.")}>
      <FormGrid>
        {text('name', tx('İş adı'), values.name, name => set({ name }))}
        {job && <div className="md:col-span-2"><EngineGroup engine={configuration.engine} onEngine={form.changeEngine} /></div>}
        {manual && <div className="min-w-0 space-y-1">
          {select('agentId', tx('Yedekleme bilgisayarı'), values.agentId, [['', tx('Yedekleme bilgisayarı seçin')], ...(values.agentId && !agents.data?.some(item => item.agentId === values.agentId) ? [[values.agentId, `${tx('Adsız bilgisayar')} · ${values.agentId}`] as const] : []), ...(agents.data ?? []).map(item => [item.agentId, `${item.hostName ?? tx('Adsız bilgisayar')} · ${statusLabel('agent', item.operationalStatus)}`] as const)], agentId => set({ agentId }))}
          {agents.isPending && <p role="status" className="text-sm text-text-muted">{tx("Bilgisayar listesi yükleniyor…")}</p>}{agents.error && <p role="alert" className="text-sm text-error">{errorMessage(agents.error)}</p>}
        </div>}
      </FormGrid>
    </Question>
    {manual ? <Question compact={Boolean(job)} title={isFiles ? tx('Hangi dosya ve klasörleri yedekleyeceğiz?') : tx('Hangi veritabanını yedekleyeceğiz?')} why={tx('Yedeğin neyi kapsayacağını ve oraya nasıl ulaşılacağını bilmemiz gerekir.')}>
      {!job && <Button type="button" variant="ghost" size="sm" className="mb-4" onClick={() => set({ sourceMode: 'discovery' })}>{tx("Otomatik keşiften seç")}</Button>}
      {!job && <EngineGroup engine={configuration.engine} onEngine={form.changeEngine} />}
      {isFiles ? <FileSourceGroup key={configuration.engine} {...group} edit={Boolean(job)} /> : <DatabaseSourceGroup key={configuration.engine} {...group} />}
    </Question>
    : <Question compact={Boolean(job)} title={tx('Hangi veritabanını yedekleyeceğiz?')} why={tx('Yedeğin neyi kapsayacağını ve oraya nasıl ulaşılacağını bilmemiz gerekir.')}>
      {picked ? <DiscoveredSource form={form} /> : <DiscoverySourceChoice form={form} />}
      {sourceError && <p role="alert" className="mt-3 text-sm text-error">{sourceError}</p>}
    </Question>}
    {(!job || !isFiles) && <Advanced open={withErrors('enabled', 'configuration.port')}>
      {!job && check(tx('İş etkin'), values.enabled, enabled => set({ enabled }))}
      {manual && !isFiles && number('configuration.port', isPg ? tx('Port (varsayılan 5432)') : tx('Port (isteğe bağlı)'), configuration.port, port => setConfiguration({ ...configuration, port }))}
      {manual && !isFiles && check(tx('Bağlantıyı şifrele'), configuration.encrypt, encrypt => setConfiguration({ ...configuration, encrypt }))}
      {manual && !isFiles && check(tx('Sunucu sertifikasına güven'), configuration.trustServerCertificate, trustServerCertificate => setConfiguration({ ...configuration, trustServerCertificate }))}
    </Advanced>}
  </>
}
