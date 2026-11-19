import { tx, useLanguage } from '@/lib/i18n'
import { CheckField } from '@/components/ui/check-field'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { Loader2 } from 'lucide-react'
import { linkClass } from '@/components/ui/form-styles'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Disclosure } from '@/components/ui/disclosure'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { useAgents } from '@/features/agents/hooks'
import { AccessPill } from '@/features/discovery/access-pill'
import { DatabaseTable } from '@/features/discovery/database-table'
import { useDiscoveryAgents, useDiscoveryInstance, useSaveCredential, useVerifyInstance } from '@/features/discovery/hooks'
import { canCreateJob, databaseSelectable, engineLabels, plainReasonText, reasonText, sourceLabels, TLS_UNAVAILABLE } from '@/features/discovery/status'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { DatabaseLoginFields } from '@/features/backup-jobs/database-login-fields'
import { loginErrors } from '@/features/backup-jobs/database-login-validation'
import type { DatabaseLogin } from '@/features/backup-jobs/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { describeError, describeFailure, failureMessage } from '@/lib/api/failure-messages'
import { discoveredDatabasesPath } from '@/lib/routes'
import { formatDateTime } from '@/lib/format'

type CredentialActions = { save: ReturnType<typeof useSaveCredential>; verify: ReturnType<typeof useVerifyInstance> }

/** The login form. It owns the typed values, so they survive the section changing from the open card (login needed) to the closed disclosure (login works). */
function CredentialSection({ instance, save, verify, needsLogin }: { instance: DiscoveryInstanceDetail; needsLogin: boolean } & CredentialActions) {
  useLanguage()
  const isMssql = instance.engine === 'mssql'
  const [login, setLogin] = useState<DatabaseLogin | null>(null)
  const [submitted, setSubmitted] = useState(false)
  const hasStored = Boolean(instance.credential.credentialReference)
  const [integrated, setIntegrated] = useState(instance.credential.integratedSecurity)
  const usesPassword = !(isMssql && integrated)
  const loginProblem = usesPassword && submitted ? loginErrors(login, hasStored) : undefined
  const [encrypt, setEncrypt] = useState(instance.credential.encrypt)
  const description = tx("Veritabanlarını listelemek için bu sunucuda kullanılacak kullanıcı. Parola şifrelenerek saklanır ve bir daha gösterilmez.")
  const form = <form className="space-y-3" noValidate onSubmit={event => {
    event.preventDefault()
    setSubmitted(true)
    if (usesPassword && loginErrors(login, hasStored)) return
    // The typed login stays in the form until the screen is left, so one wrong character can be corrected and sent again.
    save.mutate({ credentialReference: usesPassword ? instance.credential.credentialReference : null, databaseLogin: usesPassword ? login : null, integratedSecurity: isMssql && integrated, encrypt, trustServerCertificate: instance.credential.trustServerCertificate },
      { onSuccess: () => verify.mutate(instance.id) })
  }}>
    {usesPassword && <DatabaseLoginFields idPrefix="credential" login={login} hasStored={hasStored} errors={loginProblem} onChange={setLogin} />}
    {isMssql && <CheckField label={tx("Windows hesabıyla giriş yap")} checked={integrated} onChange={event => setIntegrated(event.target.checked)} />}
    <CheckField label={tx("Bağlantıyı şifrele")} checked={encrypt} onChange={event => setEncrypt(event.target.checked)} />
    <div className="flex flex-wrap gap-2">
      <Button type="submit" disabled={save.isPending || verify.isPending}>{(save.isPending || verify.isPending) && <Loader2 aria-hidden="true" className="animate-spin" />}{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet ve doğrula')}</Button>
    </div>
  </form>
  return needsLogin
    ? <Section title={tx("Veritabanı sunucusuna giriş")} description={description}>{form}</Section>
    : <Disclosure title={tx("Giriş bilgilerini değiştir")}><div className="space-y-3"><p className="text-text-muted">{description}</p>{form}</div></Disclosure>
}

/** The bound login stays as it is; the trust is saved on the server's binding, so it holds for every later scan, then the scan runs again. */
function CertificateNotice({ instance, save, verify }: { instance: DiscoveryInstanceDetail } & CredentialActions) {
  useLanguage()
  const { credentialReference, integratedSecurity, encrypt } = instance.credential
  const failure = describeFailure(instance.accessReasonCode)
  const trust = { note: tx("Bu sunucunun sertifikası kalıcı olarak güvenilir sayılacak."), pending: save.isPending || verify.isPending, onConfirm: () => save.mutate(
    { credentialReference, databaseLogin: null, integratedSecurity, encrypt, trustServerCertificate: true }, { onSuccess: () => verify.mutate(instance.id) }) }
  return <div className="space-y-3">
    <ErrorNotice {...failure} trust={trust} />
  </div>
}

/** The one explanation of why the databases cannot be listed: the reason, what it means for the list below, and the next step (the login form). */
function AccessNotice({ instance, save, verify }: { instance: DiscoveryInstanceDetail } & CredentialActions) {
  useLanguage()
  if (instance.accessReasonCode && describeFailure(instance.accessReasonCode).certificateUntrusted) return <CertificateNotice instance={instance} save={save} verify={verify} />
  // Not a login problem: the hint to re-check the login would send the user the wrong way, and no button turns the encryption off for them.
  if (instance.accessReasonCode === TLS_UNAVAILABLE) return <ErrorNotice {...describeFailure(instance.accessReasonCode)} title={tx("Şifreli bağlantı kurulamadı")} />
  if (instance.access === 'CredentialsRequired') return <Notice tone="warning">{tx("Veritabanlarını listelemek için giriş bilgilerini girin.")}</Notice>
  const reason = plainReasonText(instance.accessReasonCode)
  return <Notice tone="warning">
    {reason && <p>{reason}</p>}
    <p>{tx("Giriş bilgilerini kontrol edip yeniden kaydedin.")}</p>
  </Notice>
}

/** What saving or verifying the login produced; it is shown with the access notice so every warning and error of this page sits in one place. */
function CredentialFeedback({ error }: { error: unknown }) {
  useLanguage()
  const detail = error instanceof ApiError && Object.values(error.fields).length > 0 ? { what: failureMessage(Object.values(error.fields)[0]) } : {}
  return <ErrorNotice {...describeError(error)} {...detail} />
}

function Verifying() {
  useLanguage()
  return <p role="status" className="flex flex-wrap items-center gap-x-2 text-sm text-text"><Loader2 aria-hidden="true" className="size-4 animate-spin text-primary" />{tx("Doğrulanıyor…")}<span className="text-text-muted">{tx("En fazla 30 saniye sürebilir.")}</span></p>
}

function Databases({ instance }: { instance: DiscoveryInstanceDetail }) {
  useLanguage()
  const { tenantId } = useTenant()
  const navigate = useNavigate()
  const [selected, setSelected] = useState<string[]>([])
  if (instance.access === 'CredentialsRequired') return null
  const creatable = canCreateJob(instance)
  const picks = selected.filter(name => instance.databases.some(db => db.name === name && databaseSelectable(db, instance)))
  return <Section title={tx("Veritabanları")} description={tx("Yedeklenebilir olanları seçip yedekleme işi oluşturabilirsiniz. Sistem veritabanları ve zaten yedeklenenler seçilemez.")} flush
    actions={creatable ? <Button disabled={picks.length === 0} onClick={() => navigate(`/tenants/${tenantId}/backup-jobs/new?${new URLSearchParams([['discoveryInstance', instance.id], ...picks.map(name => ['database', name])])}`)}>{tx("Yedekleme işi oluştur")}{picks.length > 0 ? ` (${picks.length})` : ''}</Button> : undefined}>
    <DatabaseTable instance={instance} selected={picks} onChange={creatable ? setSelected : undefined} />
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
  const instance = query.data
  const { scanning } = useDiscoveryAgents(Boolean(instance), instance?.agentId)
  const computer = instance && (agents.data?.find(item => item.agentId === instance.agentId)?.hostName || tx('Adsız bilgisayar'))
  const needsLogin = instance !== undefined && instance.access !== 'Authenticated'
  const noAddress = instance !== undefined && instance.access === 'Authenticated' && !canCreateJob(instance) && instance.presence === 'Present'
  // The request is accepted at once; the result is there when the computer's scan is over (the page polls while it runs).
  // It also ends as soon as the access result differs from the one the request started from.
  const result = instance && `${instance.access}|${instance.accessReasonCode}|${instance.databasesCheckedAt}`
  const [requestedFrom, setRequestedFrom] = useState(result)
  if (!verify.isSuccess && requestedFrom !== result) setRequestedFrom(result)
  const verifying = verify.isPending || (verify.isSuccess && scanning && result === requestedFrom)
  // The access result and the last error are of the previous attempt while a new one runs: they come back with the new result.
  const busy = save.isPending || verifying
  const feedbackError = busy ? null : save.error ?? verify.error
  const accessWarning = needsLogin && !busy
  const warned = instance !== undefined && (instance.presence === 'Missing' || accessWarning || noAddress || Boolean(feedbackError))
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: tx('Bulunan veritabanları'), to: discoveredDatabasesPath(tenantId) }, { label: instance?.displayName ?? tx('Veritabanı sunucusu') }]} title={instance?.displayName ?? tx('Veritabanı sunucusu')}
      description={instance && <>{tx("Bilgisayar")}: <Link className={linkClass} to={`/tenants/${tenantId}/agents/${encodeURIComponent(instance.agentId)}`}>{computer}</Link></>}
      meta={<>
        {instance && <AccessPill status={instance.access} reasonCode={instance.accessReasonCode} />}
        {scanning && !verifying && <span role="status" className="flex items-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin" />{tx("Tarama sürüyor")}</span>}
      </>} />
    <QueryState isPending={query.isPending} error={query.error}>
      {instance && <>
        {warned && <section aria-labelledby="instance-warnings" className="space-y-3">
          <h2 id="instance-warnings" className="text-base font-semibold text-text-heading">{tx("Uyarılar")}</h2>
          {instance.presence === 'Missing' && <Notice tone="warning" title={tx("Sunucu bulunamadı")}>{tx("Son aramalarda bu veritabanı sunucusu bulunamadı; kaydı silinmedi.")}</Notice>}
          {accessWarning && <AccessNotice instance={instance} save={save} verify={verify} />}
          {noAddress && <Notice tone="warning">{tx("Bu sunucunun adresi bilinmediği için yedekleme işi oluşturulamıyor.")}</Notice>}
          {feedbackError && <CredentialFeedback error={feedbackError} />}
        </section>}
        {verifying && <Verifying />}
        <CredentialSection key={instance.id} instance={instance} save={save} verify={verify} needsLogin={needsLogin} />
        <Databases key={`${instance.id}:db`} instance={instance} />
        <TechnicalDetails items={technicalItems(instance)} />
      </>}
    </QueryState>
  </div>
}
