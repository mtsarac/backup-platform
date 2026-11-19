import { tx, useLanguage } from '@/lib/i18n'
import { CheckField } from '@/components/ui/check-field'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { Loader2 } from 'lucide-react'
import { linkClass } from '@/components/ui/form-styles'
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
import { describeError, describeFailure, failureMessage } from '@/lib/api/failure-messages'
import { discoveredDatabasesPath } from '@/lib/routes'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

type CredentialActions = { save: ReturnType<typeof useSaveCredential>; verify: ReturnType<typeof useVerifyInstance> }

function CredentialForm({ instance, save, verify }: { instance: DiscoveryInstanceDetail } & CredentialActions) {
  useLanguage()
  const { canOperate } = useTenant()
  const isMssql = instance.engine === 'mssql'
  const [login, setLogin] = useState<DatabaseLogin | null>(null)
  const [submitted, setSubmitted] = useState(false)
  const hasStored = Boolean(instance.credential.credentialReference)
  const [integrated, setIntegrated] = useState(instance.credential.integratedSecurity)
  const usesPassword = !(isMssql && integrated)
  const loginProblem = usesPassword && submitted ? loginErrors(login, hasStored) : undefined
  const [encrypt, setEncrypt] = useState(instance.credential.encrypt)
  return <Section title={tx("Veritabanı sunucusuna giriş")} description={tx("Veritabanlarını listelemek için bu sunucuda kullanılacak kullanıcı. Parola şifrelenerek saklanır ve bir daha gösterilmez.")}>
    <form className="space-y-3" noValidate onSubmit={event => {
      event.preventDefault()
      setSubmitted(true)
      if (usesPassword && loginErrors(login, hasStored)) return
      // The typed login stays in the form until the screen is left, so one wrong character can be corrected and sent again.
      save.mutate({ credentialReference: usesPassword ? instance.credential.credentialReference : null, databaseLogin: usesPassword ? login : null, integratedSecurity: isMssql && integrated, encrypt, trustServerCertificate: instance.credential.trustServerCertificate },
        { onSuccess: () => verify.mutate(instance.id) })
    }}>
      {usesPassword && <DatabaseLoginFields idPrefix="credential" login={login} hasStored={hasStored} disabled={!canOperate} errors={loginProblem} onChange={setLogin} />}
      {isMssql && <CheckField label={tx("Windows hesabıyla giriş yap")} checked={integrated} disabled={!canOperate} onChange={event => setIntegrated(event.target.checked)} />}
      <CheckField label={tx("Bağlantıyı şifrele")} checked={encrypt} disabled={!canOperate} onChange={event => setEncrypt(event.target.checked)} />
      {canOperate && <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={save.isPending || verify.isPending}>{(save.isPending || verify.isPending) && <Loader2 aria-hidden="true" className="animate-spin" />}{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet ve doğrula')}</Button>
      </div>}
    </form>
  </Section>
}

/** The bound login stays as it is; the trust is saved on the server's binding, so it holds for every later scan, then the scan runs again. */
function CertificateNotice({ instance, save, verify }: { instance: DiscoveryInstanceDetail } & CredentialActions) {
  useLanguage()
  const { canOperate } = useTenant()
  const { credentialReference, integratedSecurity, encrypt } = instance.credential
  const failure = describeFailure(instance.accessReasonCode)
  const trust = canOperate ? { note: tx("Bu sunucunun sertifikası kalıcı olarak güvenilir sayılacak."), pending: save.isPending || verify.isPending, onConfirm: () => save.mutate(
    { credentialReference, databaseLogin: null, integratedSecurity, encrypt, trustServerCertificate: true }, { onSuccess: () => verify.mutate(instance.id) }) } : undefined
  return <div className="space-y-3">
    <ErrorNotice {...failure} action={canOperate ? failure.action : tx("Yöneticinize danışın.")} trust={trust} />
  </div>
}

/** The one explanation of why the databases cannot be listed: the reason, what it means for the list below, and the next step (the login form). */
function AccessNotice({ instance, save, verify }: { instance: DiscoveryInstanceDetail } & CredentialActions) {
  useLanguage()
  const { canOperate } = useTenant()
  if (instance.accessReasonCode && describeFailure(instance.accessReasonCode).certificateUntrusted) return <CertificateNotice instance={instance} save={save} verify={verify} />
  if (instance.access === 'CredentialsRequired') return <Notice tone="warning">{tx("Bu sunucuya giriş bilgisi verilmediği için veritabanları listelenemedi. Sunucuda veritabanı olmadığı anlamına gelmez; aşağıdaki bilgileri girin.")}</Notice>
  const reason = plainReasonText(instance.accessReasonCode)
  return <Notice tone="warning">
    {reason && <p>{reason}</p>}
    <p>{tx("Son denemede sunucuya giriş yapılamadı; aşağıdaki liste eski bir aramadan kalmadır ve yedekleme işi oluşturmak için sunulmaz.")}</p>
    {canOperate && <p>{tx("Giriş bilgilerini kontrol edip yeniden kaydedin; bağlantı otomatik doğrulanır.")}</p>}
  </Notice>
}

/** What saving or verifying the login produced; it is shown with the access notice so every warning and error of this page sits in one place. */
function CredentialFeedback({ save, verify }: CredentialActions) {
  useLanguage()
  const error = save.error ?? verify.error
  const detail = error instanceof ApiError && Object.values(error.fields).length > 0 ? { what: failureMessage(Object.values(error.fields)[0]) } : {}
  return <>
    {error && <ErrorNotice {...describeError(error)} {...detail} />}
    {verify.isSuccess && !error && <Notice tone="info">{tx("Doğrulama istendi; sonuç bilgisayarın bir sonraki bağlantısında gelir.")}</Notice>}
  </>
}

function Databases({ instance }: { instance: DiscoveryInstanceDetail }) {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const navigate = useNavigate()
  const [selected, setSelected] = useState<string[]>([])
  if (instance.access === 'CredentialsRequired') return null
  const creatable = canCreateJob(instance)
  const picks = selected.filter(name => instance.databases.some(db => db.name === name && databaseSelectable(db, instance)))
  return <Section title={tx("Veritabanları")} description={tx("Yedeklenebilir olanları seçip yedekleme işi oluşturabilirsiniz. Sistem veritabanları ve zaten yedeklenenler seçilemez.")} flush
    actions={canOperate && creatable ? <Button disabled={picks.length === 0} onClick={() => navigate(`/tenants/${tenantId}/backup-jobs/new?${new URLSearchParams([['discoveryInstance', instance.id], ...picks.map(name => ['database', name])])}`)}>{tx("Yedekleme işi oluştur")}{picks.length > 0 ? ` (${picks.length})` : ''}</Button> : undefined}>
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
  return pairs.filter((pair): pair is [string, string] => Boolean(pair[1])).map(([label, value]) => ({ label, value }))
}

export function InstanceDetailPage() {
  useLanguage()
  const { instanceId = '' } = useParams()
  const { tenantId } = useTenant()
  const query = useDiscoveryInstance(instanceId)
  const save = useSaveCredential(instanceId)
  const verify = useVerifyInstance()
  const agents = useAgents(tenantId)
  const { scanning } = useDiscoveryAgents()
  const instance = query.data
  const computer = instance && (agents.data?.find(item => item.agentId === instance.agentId)?.hostName || tx('Adsız bilgisayar'))
  const needsLogin = instance && instance.access !== 'Authenticated'
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Bulunan veritabanları'), to: discoveredDatabasesPath(tenantId) }, { label: instance?.displayName ?? tx('Veritabanı sunucusu') }]} title={instance?.displayName ?? tx('Veritabanı sunucusu')}
      description={instance && <>{tx("Bilgisayar")}: <Link className={linkClass} to={`/tenants/${tenantId}/agents/${encodeURIComponent(instance.agentId)}`}>{computer}</Link></>}
      meta={<>
        {instance && <StatusPill domain="access" status={instance.access} />}
        {scanning && <span role="status" className="flex items-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin" />{tx("Tarama sürüyor")}</span>}
      </>} />
    <QueryState isPending={query.isPending} error={query.error}>
      {instance && <>
        <section aria-label={tx("Uyarılar")} className="space-y-3 empty:hidden">
          {instance.presence === 'Missing' && <Notice tone="warning" title={tx("Sunucu bulunamadı")}>{tx("Son aramalarda bu veritabanı sunucusu bulunamadı; kaydı silinmedi.")}</Notice>}
          {needsLogin && <AccessNotice instance={instance} save={save} verify={verify} />}
          {instance.access === 'Authenticated' && !canCreateJob(instance) && instance.presence === 'Present' && <Notice tone="warning">{tx("Bu sunucunun adresi bilinmediği için yedekleme işi oluşturulamıyor.")}</Notice>}
          <CredentialFeedback save={save} verify={verify} />
        </section>
        {needsLogin && <CredentialForm key={instance.id} instance={instance} save={save} verify={verify} />}
        <Databases key={`${instance.id}:db`} instance={instance} />
        <TechnicalDetails items={technicalItems(instance)}>{!needsLogin && <CredentialForm key={instance.id} instance={instance} save={save} verify={verify} />}</TechnicalDetails>
      </>}
    </QueryState>
  </div>
}
