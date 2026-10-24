import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { Loader2 } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { DatabaseTable } from '@/features/discovery/database-table'
import { useDiscoveryAgents, useDiscoveryInstance, useSaveCredential, useVerifyInstance } from '@/features/discovery/hooks'
import { canCreateJob, databaseSelectable, engineLabels, reasonText, sourceLabels } from '@/features/discovery/status'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { DatabaseLoginFields } from '@/features/backup-jobs/database-login-fields'
import { loginErrors } from '@/features/backup-jobs/database-login-validation'
import type { DatabaseLogin } from '@/features/backup-jobs/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { describeError, failureMessage } from '@/lib/api/failure-messages'
import { discoveryPath } from '@/lib/routes'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

function CredentialForm({ instance }: { instance: DiscoveryInstanceDetail }) {
  useLanguage()
  const { canOperate } = useTenant()
  const save = useSaveCredential(instance.id)
  const verify = useVerifyInstance()
  const isMssql = instance.engine === 'mssql'
  const [login, setLogin] = useState<DatabaseLogin | null>(null)
  const [submitted, setSubmitted] = useState(false)
  const hasStored = Boolean(instance.credential.credentialReference)
  const [integrated, setIntegrated] = useState(instance.credential.integratedSecurity)
  const usesPassword = !(isMssql && integrated)
  const loginProblem = usesPassword && submitted ? loginErrors(login, hasStored) : undefined
  const [encrypt, setEncrypt] = useState(instance.credential.encrypt)
  const [trust, setTrust] = useState(instance.credential.trustServerCertificate)
  const otherError = save.error ? { ...describeError(save.error), ...(save.error instanceof ApiError && Object.values(save.error.fields).length > 0 ? { what: failureMessage(Object.values(save.error.fields)[0]) } : {}) } : undefined
  const verifyStarted = verify.isSuccess
  return <Section title={tx("Bağlantı kimlik bilgisi")} description={tx("Bu Instance için kullanılacak veritabanı kullanıcısı. Parola şifrelenerek saklanır ve bir daha gösterilmez.")}>
    <form className="space-y-3" noValidate onSubmit={event => {
      event.preventDefault()
      setSubmitted(true)
      if (usesPassword && loginErrors(login, hasStored)) return
      save.mutate({ credentialReference: usesPassword ? instance.credential.credentialReference : null, databaseLogin: usesPassword ? login : null, integratedSecurity: isMssql && integrated, encrypt, trustServerCertificate: trust })
    }}>
      {usesPassword && <DatabaseLoginFields idPrefix="credential" login={login} hasStored={hasStored} disabled={!canOperate} errors={loginProblem} onChange={setLogin} />}
      {isMssql && <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={integrated} disabled={!canOperate} onChange={event => setIntegrated(event.target.checked)} />{tx("Windows kimlik doğrulaması (integrated security)")}</label>}
      <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={encrypt} disabled={!canOperate} onChange={event => setEncrypt(event.target.checked)} />{tx("Bağlantıyı şifrele")}</label>
      <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={trust} disabled={!canOperate} onChange={event => setTrust(event.target.checked)} />{tx("Sunucu sertifikasına güven")}</label>
      {otherError && <ErrorNotice {...otherError} />}
      {save.isSuccess && <Notice tone="success">{tx("Veritabanı girişi kaydedildi. Veritabanlarını listelemek için bağlantıyı doğrulayın.")}</Notice>}
      {verifyStarted && <Notice tone="info">{tx("Doğrulama taraması istendi; sonuç Agent'ın bir sonraki heartbeat'inde gelir.")}</Notice>}
      {verify.error && <ErrorNotice error={verify.error} />}
      {canOperate && <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={save.isPending}>{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}</Button>
        <Button type="button" variant="outline" disabled={!instance.credential.bound || verify.isPending} onClick={() => verify.mutate(instance.id)}>{verify.isPending && <Loader2 aria-hidden="true" className="animate-spin" />}{tx("Bağlantıyı Doğrula")}</Button>
      </div>}
    </form>
  </Section>
}

function Databases({ instance }: { instance: DiscoveryInstanceDetail }) {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const navigate = useNavigate()
  const [selected, setSelected] = useState<string[]>([])
  if (instance.access === 'CredentialsRequired') return <Notice tone="warning" title={tx("Kimlik bilgisi gerekli")}>{tx("Bu Instance için kimlik bilgisi bağlanmadığından veritabanları listelenmedi. Instance'ın hiç veritabanı içermediği anlamına gelmez.")}</Notice>
  const creatable = canCreateJob(instance)
  const picks = selected.filter(name => instance.databases.some(db => db.name === name && databaseSelectable(db, instance)))
  const create = () => navigate(`/tenants/${tenantId}/backup-jobs/new?${new URLSearchParams([['discoveryInstance', instance.id], ...picks.map(name => ['database', name])])}`)
  return <Section title={tx("Veritabanları")} description={instance.databasesCheckedAt ? tx("Son kontrol: {0}", { "0": formatDateTime(instance.databasesCheckedAt, tx('Henüz bildirilmedi')) }) : tx('Henüz kontrol edilmedi.')} flush
    actions={canOperate && creatable ? <Button disabled={picks.length === 0} onClick={create}>{tx("Yedekleme İşi Oluştur")}{picks.length > 0 ? ` (${picks.length})` : ''}</Button> : undefined}>
    {instance.access !== 'Authenticated' && <div className="p-4"><Notice tone="warning" title={tx("Liste güncel olmayabilir")}>{tx("Son denemede kimlik doğrulanamadı; aşağıdaki liste eski bir taramadan kalmadır ve Backup Job oluşturmak için sunulmaz.")}</Notice></div>}
    {instance.access === 'Authenticated' && !creatable && <div className="p-4"><Notice tone="warning">{tx("Bu Instance için host/port bilinmediğinden Backup Job oluşturulamaz.")}</Notice></div>}
    <DatabaseTable instance={instance} selected={picks} onChange={canOperate && creatable ? setSelected : undefined} />
  </Section>
}

export function InstanceDetailPage() {
  useLanguage()
  const { instanceId = '' } = useParams()
  const { tenantId } = useTenant()
  const query = useDiscoveryInstance(instanceId)
  const { scanning } = useDiscoveryAgents()
  const instance = query.data
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Keşfedilen Veritabanları'), to: discoveryPath(tenantId) }, { label: instance?.displayName ?? 'Instance' }]} title={instance?.displayName ?? 'Instance'} meta={scanning ? <span role="status" className="flex items-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin" />{tx("Tarama sürüyor")}</span> : undefined} />
    <QueryState isPending={query.isPending} error={query.error}>
      {instance && <>
        {instance.presence === 'Missing' && <Notice tone="warning" title={tx("Instance kayboldu")}>{tx("Son taramalarda bu Instance bulunamadı; kayıt silinmedi.")}</Notice>}
        <Section title={tx("Bilgiler")}><Facts columns={3}>
          <Fact label={tx("Motor")}>{engineLabels[instance.engine]}</Fact>
          <Fact label={tx("Kaynak")}>{sourceLabels[instance.source] ?? instance.source}</Fact>
          <Fact label={tx("Adres")}>{instance.host}{instance.port != null ? `:${instance.port}` : ''}</Fact>
          <Fact label={tx("Sürüm")}>{instance.version ?? '—'}</Fact>
          <Fact label={tx("Edition")}>{instance.edition ?? '—'}</Fact>
          <Fact label="Platform">{instance.hostPlatform ?? '—'}</Fact>
          {instance.containerName && <Fact label={tx("Container")}>{instance.containerName}{instance.containerImage ? ` (${instance.containerImage})` : ''}</Fact>}
          <Fact label={tx("Erişilebilirlik")}><StatusPill domain="reachability" status={instance.reachability} /></Fact>
          <Fact label={tx("Erişim")}><div className="space-y-1"><StatusPill domain="access" status={instance.access} />{reasonText(instance.accessReasonCode) && <p className="text-xs text-text-muted">{reasonText(instance.accessReasonCode)}</p>}</div></Fact>
          <Fact label={tx("Full Backup")}><div className="space-y-1"><StatusPill domain="fullBackup" status={instance.fullBackup} />{reasonText(instance.fullBackupReasonCode) && <p className="text-xs text-text-muted">{reasonText(instance.fullBackupReasonCode)}</p>}</div></Fact>
          <Fact label={tx("Varlık")}><StatusPill domain="presence" status={instance.presence} /></Fact>
          <Fact label={tx("İlk / son görülme")}>{formatDateTime(instance.firstSeenAt, tx('Henüz bildirilmedi'))} / {formatDateTime(instance.lastSeenAt, tx('Henüz bildirilmedi'))}</Fact>
          <Fact label={tx("Son değişiklik")}>{instance.lastChange ? <span className="flex flex-wrap items-center gap-2"><StatusPill domain="lastChange" status={instance.lastChange.kind} />{formatDateTime(instance.lastChange.at, tx('Henüz bildirilmedi'))}</span> : '—'}</Fact>
          <Fact label={tx("Veritabanı sayısı")}>{instance.databaseCounts.total}{' '}{tx("(kullanıcı")}{' '}{instance.databaseCounts.user}{tx(", yedeklenebilir")}{' '}{instance.databaseCounts.backupEligible})</Fact>
        </Facts></Section>
        <CredentialForm key={instance.id} instance={instance} />
        <Databases key={`${instance.id}:db`} instance={instance} />
      </>}
    </QueryState>
  </div>
}
