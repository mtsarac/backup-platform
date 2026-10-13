import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { agentDate } from '@/features/agents/format'
import { platformPaths } from './paths'
import { linkClass } from './styles'
import type { AuditDetailValue, PlatformAuditEntry } from './types'

const actionLabels: Record<string, string> = {
  'tenant.created': 'Tenant oluşturuldu',
  'tenant.renamed': 'Tenant yeniden adlandırıldı',
  'membership.added': 'Üyelik eklendi',
  'membership.role_changed': 'Üyelik rolü değiştirildi',
  'membership.removed': 'Üyelik kaldırıldı',
  'agent_enrollment.policy_changed': 'Agent kayıt politikası değiştirildi',
  'user.created': 'Kullanıcı oluşturuldu',
  'plan.created': 'Plan oluşturuldu',
  'plan.changed': 'Plan değiştirildi',
  'plan.enabled': 'Plan etkinleştirildi',
  'plan.disabled': 'Plan devre dışı bırakıldı',
  'subscription.assigned': 'Abonelik atandı',
  'subscription.changed': 'Abonelik planı değiştirildi',
  'subscription.suspended': 'Abonelik askıya alındı',
  'subscription.resumed': 'Abonelik yeniden etkinleştirildi',
  'subscription.storage_limit_override_changed': 'Depolama limiti override değiştirildi',
  'quota_policy.created': 'Kota politikası oluşturuldu',
  'quota_policy.changed': 'Kota politikası değiştirildi',
  'tenant_storage_usage.rebuilt': 'Depolama kullanımı baştan hesaplandı (Rebuild)',
  'tenant_storage_usage.reconcile_corrected': 'Depolama kullanımı doğrulandı ve düzeltildi (Reconcile)',
  'storage_connection.created': 'Depolama bağlantısı oluşturuldu',
  'storage_connection.updated': 'Depolama bağlantısı güncellendi',
  'storage_connection.enabled': 'Depolama bağlantısı etkinleştirildi',
  'storage_connection.disabled': 'Depolama bağlantısı devre dışı bırakıldı',
  'storage_connection.deleted': 'Depolama bağlantısı silindi',
  'storage_connection.tested': 'Depolama bağlantısı test edildi',
  'storage_connection.secret_rotated': 'Depolama bağlantısı kimlik bilgisi yenilendi',
  'storage_connection.secret_cleared': 'Depolama bağlantısı kimlik bilgisi temizlendi',
  'storage_connection.quota_scope_changed': 'Depolama bağlantısı kota kapsamı değiştirildi',
  'auth.login_succeeded': 'Kullanıcı girişi başarılı',
  'auth.login_failed': 'Kullanıcı girişi başarısız',
  'auth.logout': 'Kullanıcı çıkışı yapıldı',
  'agent.enrollment_token_created': 'Agent kayıt tokenı oluşturuldu',
  'agent.enrollment_token_revoked': 'Agent kayıt tokenı iptal edildi',
  'agent.enrolled': 'Agent sisteme kaydoldu',
  'agent.credential_rotation_started': 'Agent credential yenileme başlatıldı',
  'agent.credential_rotation_confirmed': 'Agent credential yenileme onaylandı',
  'agent.revoked': 'Agent yetkisi iptal edildi',
  'backup_job.created': 'Yedekleme görevi oluşturuldu',
  'backup_job.updated': 'Yedekleme görevi güncellendi',
  'backup_job.schedule_changed': 'Yedekleme zamanlaması değiştirildi',
  'backup_job.schedule_removed': 'Yedekleme zamanlaması kaldırıldı',
  'backup_job.retention_changed': 'Yedekleme saklama politikası değiştirildi',
  'backup_job.enabled': 'Yedekleme görevi etkinleştirildi',
  'backup_job.disabled': 'Yedekleme görevi devre dışı bırakıldı',
  'backup_run.triggered': 'Yedekleme çalıştırması başlatıldı',
  'backup_run.retry_requested': 'Yedekleme tekrar denemesi istendi',
  'backup_run.cancel_requested': 'Yedekleme iptali istendi',
  'backup_run.completed': 'Yedekleme tamamlandı',
  'backup_artifact.delete_requested': 'Yedekleme silme talebi oluşturuldu',
  'backup_artifact.delete_cancelled': 'Yedekleme silme talebi iptal edildi',
  'backup_replica.physical_delete_completed': 'Yedekleme kopyası fiziksel olarak silindi',
  'backup_run.attempt_completed': 'Yedekleme denemesi sonuçlandı',
  'backup_schedule.occurrence_decided': 'Zamanlanmış çalıştırma kararı verildi',
  'backup_artifact.retention_applied': 'Yedekleme saklama süresi doldu',
  'backup_replica.physical_delete_failed': 'Yedekleme kopyası silinemedi',
  'backup_replica.orphan_recorded': 'Sahipsiz yedekleme kopyası kaydedildi',
  'backup_replica.quarantined': 'Yedekleme kopyası karantinaya alındı',
  'backup_storage.orphan_delete_completed': 'Sahipsiz depolama nesnesi silindi',
}

const actorLabels: Record<PlatformAuditEntry['actorKind'], string> = {
  PlatformAdmin: 'PlatformAdmin',
  TenantAdministrator: 'Tenant yöneticisi',
  System: 'Sistem',
  User: 'Kullanıcı',
  Anonymous: 'Anonim',
}

function auditActionLabel(action: string) {
  return Object.hasOwn(actionLabels, action) ? actionLabels[action] : action
}

function detailValue(value: AuditDetailValue) {
  if (value === null) return '—'
  if (typeof value === 'boolean') return value ? 'Evet' : 'Hayır'
  return String(value)
}

function Details({ details }: { details: PlatformAuditEntry['details'] }) {
  const entries = Object.entries(details ?? {})
  if (entries.length === 0) return <span className="text-text-muted">—</span>
  return <dl className="space-y-0.5 text-xs">{entries.map(([key, value]) => <div key={key} className="flex flex-wrap gap-1"><dt className="text-text-muted">{key}:</dt><dd className="break-all">{detailValue(value)}</dd></div>)}</dl>
}

export function AuditTable({ rows, emptyMessage, showTenant = true }: { rows: readonly PlatformAuditEntry[]; emptyMessage: string; showTenant?: boolean }) {
  const columns: Column<PlatformAuditEntry>[] = [
    { key: 'at', header: 'Zaman', render: (entry) => agentDate(entry.at) },
    { key: 'action', header: 'İşlem', wrap: true, render: (entry) => <span className="font-medium">{auditActionLabel(entry.action)}</span> },
    { key: 'actor', header: 'Yapan', wrap: true, render: (entry) => <div className="flex flex-wrap items-center gap-1.5"><span>{entry.actorDisplayName ?? 'Bilinmeyen kullanıcı'}</span><Badge tone={entry.actorKind === 'PlatformAdmin' ? 'warning' : entry.actorKind === 'System' || entry.actorKind === 'Anonymous' ? 'neutral' : 'info'}>{actorLabels[entry.actorKind]}</Badge></div> },
    ...(showTenant ? [{ key: 'tenant', header: 'Tenant', wrap: true, render: (entry: PlatformAuditEntry) => entry.tenantId ? <Link className={linkClass} to={platformPaths.tenant(entry.tenantId)}>{entry.tenantName ?? 'Tenant'}</Link> : <span className="text-text-muted">—</span> }] : []),
    { key: 'target', header: 'Hedef', wrap: true, render: (entry) => entry.targetType },
    { key: 'details', header: 'Ayrıntılar', wrap: true, render: (entry) => <Details details={entry.details} /> },
  ]
  return <DataTable rows={rows} columns={columns} keyExtractor={(entry) => entry.id} emptyMessage={emptyMessage} />
}
