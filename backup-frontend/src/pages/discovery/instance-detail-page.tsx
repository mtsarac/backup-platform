import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { Loader2 } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { useAgents } from '@/features/agents/hooks'
import { DatabaseTable } from '@/features/discovery/database-table'
import { useDiscoveryAgents, useDiscoveryInstance, useSaveCredential, useVerifyInstance } from '@/features/discovery/hooks'
import { canCreateJob, databaseSelectable, engineLabels, plainReasonText, reasonText, sourceLabels } from '@/features/discovery/status'
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
  return <Section title={tx("Veritabanı sunucusuna giriş")} description={tx("Veritabanlarını listelemek için bu sunucuda kullanılacak kullanıcı. Parola şifrelenerek saklanır ve bir daha gösterilmez.")}>
    <form className="space-y-3" noValidate onSubmit={event => {
      event.preventDefault()
      setSubmitted(true)
      if (usesPassword && loginErrors(login, hasStored)) return
      save.mutate({ credentialReference: usesPassword ? instance.credential.credentialReference : null, databaseLogin: usesPassword ? login : null, integratedSecurity: isMssql && integrated, encrypt, trustServerCertificate: trust })
      setLogin(null)
      setSubmitted(false)
    }}>
      {usesPassword && <DatabaseLoginFields idPrefix="credential" login={login} hasStored={hasStored} disabled={!canOperate} errors={loginProblem} onChange={setLogin} />}
      {isMssql && <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={integrated} disabled={!canOperate} onChange={event => setIntegrated(event.target.checked)} />{tx("Windows hesabıyla giriş yap")}</label>}
      <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={encrypt} disabled={!canOperate} onChange={event => setEncrypt(event.target.checked)} />{tx("Bağlantıyı şifrele")}</label>
      <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={trust} disabled={!canOperate} onChange={event => setTrust(event.target.checked)} />{tx("Sunucu sertifikasına güven")}</label>
      {otherError && <ErrorNotice {...otherError} />}
      {save.isSuccess && <Notice tone="success">{tx("Veritabanı girişi kaydedildi. Veritabanlarını listelemek için bağlantıyı doğrulayın.")}</Notice>}
      {verifyStarted && <Notice tone="info">{tx("Doğrulama istendi; sonuç bilgisayarın bir sonraki bağlantısında gelir.")}</Notice>}
      {verify.error && <ErrorNotice error={verify.error} />}
      {canOperate && <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={save.isPending}>{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}</Button>
        <Button type="button" variant="outline" disabled={!instance.credential.bound || verify.isPending} onClick={() => verify.mutate(instance.id)}>{verify.isPending && <Loader2 aria-hidden="true" className="animate-spin" />}{tx("Bağlantıyı doğrula")}</Button>
      </div>}
    </form>
  </Section>
}

function Databases({ instance }: { instance: DiscoveryInstanceDetail }) {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const navigate = useNavigate()
  const [selected, setSelected] = useState<string[]>([])
  if (instance.access === 'CredentialsRequired') return <Notice tone="warning" title={tx("Kimlik bilgisi gerekli")}>{tx("Bu sunucuya giriş bilgisi verilmediği için veritabanları listelenemedi. Sunucuda veritabanı olmadığı anlamına gelmez; yukarıdaki bilgileri girin.")}</Notice>
  const creatable = canCreateJob(instance)
  const picks = selected.filter(name => instance.databases.some(db => db.name === name && databaseSelectable(db, instance)))
  return <Section title={tx("Veritabanları")} description={tx("Yedeklenebilir olanları seçip yedekleme işi oluşturabilirsiniz. Sistem veritabanları ve zaten yedeklenenler seçilemez.")} flush
    actions={canOperate && creatable ? <Button disabled={picks.length === 0} onClick={() => navigate(`/tenants/${tenantId}/backup-jobs/new?${new URLSearchParams([['discoveryInstance', instance.id], ...picks.map(name => ['database', name])])}`)}>{tx("Yedekleme işi oluştur")}{picks.length > 0 ? ` (${picks.length})` : ''}</Button> : undefined}>
    {instance.access !== 'Authenticated' && <div className="p-4"><Notice tone="warning" title={tx("Liste güncel olmayabilir")}>{tx("Son denemede sunucuya giriş yapılamadı; aşağıdaki liste eski bir aramadan kalmadır ve yedekleme işi oluşturmak için sunulmaz.")}</Notice></div>}
    {instance.access === 'Authenticated' && !creatable && <div className="p-4"><Notice tone="warning">{tx("Bu sunucunun adresi bilinmediği için yedekleme işi oluşturulamıyor.")}</Notice></div>}
    {!canOperate && <p className="p-4 text-sm text-text-muted">{tx("Yöneticinize danışın")}</p>}
    <DatabaseTable instance={instance} selected={picks} onChange={canOperate && creatable ? setSelected : undefined} />
  </Section>
}

function technicalItems(instance: DiscoveryInstanceDetail): TechnicalItem[] {
  const unknown = tx('Henüz bildirilmedi')
  const pairs: [string, string | null | undefined][] = [
    [tx("Sunucu kimliği"), instance.id], [tx("Sunucu anahtarı"), instance.instanceKey], [tx("Motor"), engineLabels[instance.engine]], [tx("Kaynak"), sourceLabels[instance.source] ?? instance.source],
    [tx("Bilgisayar kimliği"), instance.agentId],
    [tx("Adres"), `${instance.host}${instance.port != null ? `:${instance.port}` : ''}`], [tx("Sürüm"), instance.version], [tx("Edition"), instance.edition], [tx("Platform"), instance.hostPlatform],
    [tx("Container"), instance.containerName && `${instance.containerName}${instance.containerImage ? ` (${instance.containerImage})` : ''}`],
    [tx("Erişilebilirlik"), instance.reachability], [tx("Erişim"), [instance.access, reasonText(instance.accessReasonCode)].filter(Boolean).join(' · ')],
    [tx("Tam yedek desteği"), [instance.fullBackup, reasonText(instance.fullBackupReasonCode)].filter(Boolean).join(' · ')], [tx("Varlık"), instance.presence],
    [tx("İlk görülme"), formatDateTime(instance.firstSeenAt, unknown)], [tx("Son görülme"), formatDateTime(instance.lastSeenAt, unknown)],
    [tx("Son değişiklik"), instance.lastChange ? `${instance.lastChange.kind} · ${formatDateTime(instance.lastChange.at, unknown)}` : null],
    [tx("Son veritabanı kontrolü"), instance.databasesCheckedAt ? formatDateTime(instance.databasesCheckedAt, unknown) : null],
    [tx("Veritabanı sayısı"), tx("{0} (kullanıcı {1}, yedeklenebilir {2})", { "0": instance.databaseCounts.total, "1": instance.databaseCounts.user, "2": instance.databaseCounts.backupEligible })],
  ]
  return [
    ...pairs.filter((pair): pair is [string, string] => Boolean(pair[1])).map(([label, value]) => ({ label, value })),
    ...instance.databases.map(db => ({ label: db.name, value: [db.state, db.presence, db.fullBackup, reasonText(db.fullBackupReasonCode)].filter(Boolean).join(' · ') })),
  ]
}

export function InstanceDetailPage() {
  useLanguage()
  const { instanceId = '' } = useParams()
  const { tenantId } = useTenant()
  const query = useDiscoveryInstance(instanceId)
  const agents = useAgents(tenantId)
  const { scanning } = useDiscoveryAgents()
  const instance = query.data
  const computer = instance && (agents.data?.find(item => item.agentId === instance.agentId)?.hostName || tx('Adsız bilgisayar'))
  const accessReason = instance && plainReasonText(instance.accessReasonCode)
  const needsLogin = instance && instance.access !== 'Authenticated'
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Veritabanı bulma'), to: discoveryPath(tenantId) }, { label: instance?.displayName ?? tx('Veritabanı sunucusu') }]} title={instance?.displayName ?? tx('Veritabanı sunucusu')}
      description={computer ? tx("{0} bilgisayarında bulundu.", { "0": computer }) : undefined}
      meta={<>
        {instance && <StatusPill domain="access" status={instance.access} />}
        {scanning && <span role="status" className="flex items-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin" />{tx("Tarama sürüyor")}</span>}
      </>} />
    <QueryState isPending={query.isPending} error={query.error}>
      {instance && <>
        {instance.presence === 'Missing' && <Notice tone="warning" title={tx("Sunucu bulunamadı")}>{tx("Son aramalarda bu veritabanı sunucusu bulunamadı; kaydı silinmedi.")}</Notice>}
        {accessReason && <p className="text-sm text-text">{accessReason}</p>}
        {needsLogin && <CredentialForm key={instance.id} instance={instance} />}
        <Databases key={`${instance.id}:db`} instance={instance} />
        <TechnicalDetails items={technicalItems(instance)}>{!needsLogin && <CredentialForm key={instance.id} instance={instance} />}</TechnicalDetails>
      </>}
    </QueryState>
  </div>
}
