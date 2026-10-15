import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { Loader2 } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { agentDate } from '@/features/agents/format'
import { DatabaseTable } from '@/features/discovery/database-table'
import { useDiscoveryAgents, useDiscoveryInstance, useSaveCredential, useVerifyInstance } from '@/features/discovery/hooks'
import { AccessPill, FullBackupPill, LastChangePill, PresencePill, ReachabilityPill } from '@/features/discovery/presentation'
import { canCreateJob, databaseSelectable, engineLabels, reasonText, sourceLabels } from '@/features/discovery/status'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { inputClass } from '@/features/platform/styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { errorMessage, failureMessage } from '@/lib/api/failure-messages'
import { discoveryPath } from '@/lib/routes'

function CredentialForm({ instance }: { instance: DiscoveryInstanceDetail }) {
  const { canOperate } = useTenant()
  const save = useSaveCredential(instance.id)
  const verify = useVerifyInstance()
  const isMssql = instance.engine === 'mssql'
  const [reference, setReference] = useState(instance.credential.credentialReference ?? '')
  const [integrated, setIntegrated] = useState(instance.credential.integratedSecurity)
  const [encrypt, setEncrypt] = useState(instance.credential.encrypt)
  const [trust, setTrust] = useState(instance.credential.trustServerCertificate)
  const referenceError = save.error instanceof ApiError ? (save.error.fields.credentialReference ? 'Kimlik bilgisi referansı geçersiz. Agent üzerinde tanımlı gizli anahtar adını girin.' : Object.values(save.error.fields).map(failureMessage)[0]) : undefined
  const otherError = save.error && !referenceError ? errorMessage(save.error) : undefined
  const verifyStarted = verify.isSuccess
  return <Section title="Bağlantı kimlik bilgisi" description="Parola girilmez: yalnızca Agent üzerindeki gizli anahtarın referans adı saklanır.">
    <form className="space-y-3" noValidate onSubmit={event => { event.preventDefault(); save.mutate({ credentialReference: integrated ? null : reference.trim() || null, integratedSecurity: isMssql && integrated, encrypt, trustServerCertificate: trust }) }}>
      <Field id="credential-reference" label="Kimlik bilgisi referans adı (parola değil)" hint="Agent makinesinde BACKUP_AGENT_SECRET_<referans> olarak tanımlı gizli anahtarın adıdır; parola bu forma yazılmaz ve platformda saklanmaz." error={referenceError}>
        {control => <input {...control} className={inputClass} value={reference} disabled={!canOperate || (isMssql && integrated)} onChange={event => setReference(event.target.value)} />}
      </Field>
      {isMssql && <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={integrated} disabled={!canOperate} onChange={event => setIntegrated(event.target.checked)} />Windows kimlik doğrulaması (integrated security)</label>}
      <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={encrypt} disabled={!canOperate} onChange={event => setEncrypt(event.target.checked)} />Bağlantıyı şifrele</label>
      <label className="flex min-h-11 items-center gap-3 text-[0.9375rem] text-text"><input type="checkbox" className="size-5 accent-primary" checked={trust} disabled={!canOperate} onChange={event => setTrust(event.target.checked)} />Sunucu sertifikasına güven</label>
      {otherError && <Notice tone="error">{otherError}</Notice>}
      {save.isSuccess && <Notice tone="success">Kimlik bilgisi bağlaması kaydedildi. Veritabanlarını listelemek için bağlantıyı doğrulayın.</Notice>}
      {verifyStarted && <Notice tone="info">Doğrulama taraması istendi; sonuç Agent'ın bir sonraki heartbeat'inde gelir.</Notice>}
      {verify.error && <Notice tone="error">{errorMessage(verify.error)}</Notice>}
      {canOperate && <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={save.isPending}>{save.isPending ? 'Kaydediliyor…' : 'Kaydet'}</Button>
        <Button type="button" variant="outline" disabled={!instance.credential.bound || verify.isPending} onClick={() => verify.mutate(instance.id)}>{verify.isPending && <Loader2 aria-hidden="true" className="animate-spin" />}Bağlantıyı Doğrula</Button>
      </div>}
    </form>
  </Section>
}

function Databases({ instance }: { instance: DiscoveryInstanceDetail }) {
  const { tenantId, canOperate } = useTenant()
  const navigate = useNavigate()
  const [selected, setSelected] = useState<string[]>([])
  if (instance.access === 'CredentialsRequired') return <Notice tone="warning" title="Kimlik bilgisi gerekli">Bu Instance için kimlik bilgisi bağlanmadığından veritabanları listelenmedi. Instance'ın hiç veritabanı içermediği anlamına gelmez.</Notice>
  const creatable = canCreateJob(instance)
  const picks = selected.filter(name => instance.databases.some(db => db.name === name && databaseSelectable(db, instance)))
  const create = () => navigate(`/tenants/${tenantId}/backup-jobs/new?${new URLSearchParams([['discoveryInstance', instance.id], ...picks.map(name => ['database', name])])}`)
  return <Section title="Veritabanları" description={instance.databasesCheckedAt ? `Son kontrol: ${agentDate(instance.databasesCheckedAt)}` : 'Henüz kontrol edilmedi.'} flush
    actions={canOperate && creatable ? <Button disabled={picks.length === 0} onClick={create}>Yedekleme İşi Oluştur{picks.length > 0 ? ` (${picks.length})` : ''}</Button> : undefined}>
    {instance.access !== 'Authenticated' && <div className="p-4"><Notice tone="warning" title="Liste güncel olmayabilir">Son denemede kimlik doğrulanamadı; aşağıdaki liste eski bir taramadan kalmadır ve Backup Job oluşturmak için sunulmaz.</Notice></div>}
    {instance.access === 'Authenticated' && !creatable && <div className="p-4"><Notice tone="warning">Bu Instance için host/port bilinmediğinden Backup Job oluşturulamaz.</Notice></div>}
    <DatabaseTable instance={instance} selected={picks} onChange={canOperate && creatable ? setSelected : undefined} />
  </Section>
}

export function InstanceDetailPage() {
  const { instanceId = '' } = useParams()
  const { tenantId } = useTenant()
  const query = useDiscoveryInstance(instanceId)
  const { scanning } = useDiscoveryAgents()
  const instance = query.data
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: 'Keşfedilen Veritabanları', to: discoveryPath(tenantId) }, { label: instance?.displayName ?? 'Instance' }]} title={instance?.displayName ?? 'Instance'} meta={scanning ? <span role="status" className="flex items-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin" />Tarama sürüyor</span> : undefined} />
    <QueryState isPending={query.isPending} error={query.error}>
      {instance && <>
        {instance.presence === 'Missing' && <Notice tone="warning" title="Instance kayboldu">Son taramalarda bu Instance bulunamadı; kayıt silinmedi.</Notice>}
        <Section title="Bilgiler"><Facts columns={3}>
          <Fact label="Motor">{engineLabels[instance.engine]}</Fact>
          <Fact label="Kaynak">{sourceLabels[instance.source] ?? instance.source}</Fact>
          <Fact label="Adres">{instance.host}{instance.port != null ? `:${instance.port}` : ''}</Fact>
          <Fact label="Sürüm">{instance.version ?? '—'}</Fact>
          <Fact label="Edition">{instance.edition ?? '—'}</Fact>
          <Fact label="Platform">{instance.hostPlatform ?? '—'}</Fact>
          {instance.containerName && <Fact label="Container">{instance.containerName}{instance.containerImage ? ` (${instance.containerImage})` : ''}</Fact>}
          <Fact label="Erişilebilirlik"><ReachabilityPill value={instance.reachability} /></Fact>
          <Fact label="Erişim"><div className="space-y-1"><AccessPill value={instance.access} />{reasonText(instance.accessReasonCode) && <p className="text-xs text-text-muted">{reasonText(instance.accessReasonCode)}</p>}</div></Fact>
          <Fact label="Full Backup"><div className="space-y-1"><FullBackupPill value={instance.fullBackup} />{reasonText(instance.fullBackupReasonCode) && <p className="text-xs text-text-muted">{reasonText(instance.fullBackupReasonCode)}</p>}</div></Fact>
          <Fact label="Varlık"><PresencePill value={instance.presence} /></Fact>
          <Fact label="İlk / son görülme">{agentDate(instance.firstSeenAt)} / {agentDate(instance.lastSeenAt)}</Fact>
          <Fact label="Son değişiklik">{instance.lastChange ? <span className="flex flex-wrap items-center gap-2"><LastChangePill value={instance.lastChange.kind} />{agentDate(instance.lastChange.at)}</span> : '—'}</Fact>
          <Fact label="Veritabanı sayısı">{instance.databaseCounts.total} (kullanıcı {instance.databaseCounts.user}, yedeklenebilir {instance.databaseCounts.backupEligible})</Fact>
        </Facts></Section>
        <CredentialForm key={instance.id} instance={instance} />
        <Databases key={`${instance.id}:db`} instance={instance} />
      </>}
    </QueryState>
  </div>
}
