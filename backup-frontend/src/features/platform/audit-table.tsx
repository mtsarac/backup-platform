import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Badge } from '@/components/ui/badge'
import { platformPaths } from './paths'
import { linkClass } from '@/components/ui/form-styles'
import type { AuditDetailValue, PlatformAuditEntry } from './types'
import { formatDateTime } from '@/lib/format'

const actionLabels: Record<string, string> = {
  get 'tenant.created'() { return tx("Tenant oluşturuldu") },
  get 'tenant.renamed'() { return tx("Tenant yeniden adlandırıldı") },
  get 'membership.added'() { return tx("Üyelik eklendi") },
  get 'membership.role_changed'() { return tx("Üyelik rolü değiştirildi") },
  get 'membership.removed'() { return tx("Üyelik kaldırıldı") },
  get 'agent_enrollment.policy_changed'() { return tx("Agent kayıt politikası değiştirildi") },
  get 'user.created'() { return tx("Kullanıcı oluşturuldu") },
  get 'plan.created'() { return tx("Plan oluşturuldu") },
  get 'plan.changed'() { return tx("Plan değiştirildi") },
  get 'plan.enabled'() { return tx("Plan etkinleştirildi") },
  get 'plan.disabled'() { return tx("Plan devre dışı bırakıldı") },
  get 'subscription.assigned'() { return tx("Abonelik atandı") },
  get 'subscription.changed'() { return tx("Abonelik planı değiştirildi") },
  get 'subscription.suspended'() { return tx("Abonelik askıya alındı") },
  get 'subscription.resumed'() { return tx("Abonelik yeniden etkinleştirildi") },
  get 'subscription.storage_limit_override_changed'() { return tx("Depolama limiti override değiştirildi") },
  get 'quota_policy.created'() { return tx("Kota politikası oluşturuldu") },
  get 'quota_policy.changed'() { return tx("Kota politikası değiştirildi") },
  get 'tenant_storage_usage.rebuilt'() { return tx("Depolama kullanımı baştan hesaplandı (Rebuild)") },
  get 'tenant_storage_usage.reconcile_corrected'() { return tx("Depolama kullanımı doğrulandı ve düzeltildi (Reconcile)") },
  get 'storage_connection.created'() { return tx("Depolama bağlantısı oluşturuldu") },
  get 'storage_connection.updated'() { return tx("Depolama bağlantısı güncellendi") },
  get 'storage_connection.enabled'() { return tx("Depolama bağlantısı etkinleştirildi") },
  get 'storage_connection.disabled'() { return tx("Depolama bağlantısı devre dışı bırakıldı") },
  get 'storage_connection.deleted'() { return tx("Depolama bağlantısı silindi") },
  get 'storage_connection.tested'() { return tx("Depolama bağlantısı test edildi") },
  get 'storage_connection.secret_rotated'() { return tx("Depolama bağlantısı kimlik bilgisi yenilendi") },
  get 'storage_connection.secret_cleared'() { return tx("Depolama bağlantısı kimlik bilgisi temizlendi") },
  get 'storage_connection.quota_scope_changed'() { return tx("Depolama bağlantısı kota kapsamı değiştirildi") },
  get 'auth.login_succeeded'() { return tx("Kullanıcı girişi başarılı") },
  get 'auth.login_failed'() { return tx("Kullanıcı girişi başarısız") },
  get 'auth.logout'() { return tx("Kullanıcı çıkışı yapıldı") },
  get 'agent.enrollment_token_created'() { return tx("Agent kayıt tokenı oluşturuldu") },
  get 'agent.enrollment_token_revoked'() { return tx("Agent kayıt tokenı iptal edildi") },
  get 'agent.enrolled'() { return tx("Agent sisteme kaydoldu") },
  get 'agent.credential_rotation_started'() { return tx("Agent credential yenileme başlatıldı") },
  get 'agent.credential_rotation_confirmed'() { return tx("Agent credential yenileme onaylandı") },
  get 'agent.revoked'() { return tx("Agent yetkisi iptal edildi") },
  get 'backup_job.created'() { return tx("Yedekleme görevi oluşturuldu") },
  get 'backup_job.updated'() { return tx("Yedekleme görevi güncellendi") },
  get 'backup_job.schedule_changed'() { return tx("Yedekleme zamanlaması değiştirildi") },
  get 'backup_job.schedule_removed'() { return tx("Yedekleme zamanlaması kaldırıldı") },
  get 'backup_job.retention_changed'() { return tx("Yedekleme saklama politikası değiştirildi") },
  get 'backup_job.enabled'() { return tx("Yedekleme görevi etkinleştirildi") },
  get 'backup_job.disabled'() { return tx("Yedekleme görevi devre dışı bırakıldı") },
  get 'backup_run.triggered'() { return tx("Yedekleme çalıştırması başlatıldı") },
  get 'backup_run.retry_requested'() { return tx("Yedekleme tekrar denemesi istendi") },
  get 'backup_run.cancel_requested'() { return tx("Yedekleme iptali istendi") },
  get 'backup_run.pause_requested'() { return tx("Yedekleme duraklatma istendi") },
  get 'backup_run.resumed'() { return tx("Yedekleme devam ettirildi") },
  get 'backup_run.completed'() { return tx("Yedekleme tamamlandı") },
  get 'backup_run.size_anomaly'() { return tx("Boyut Anomalisi") },
  get 'backup_artifact.delete_requested'() { return tx("Yedekleme silme talebi oluşturuldu") },
  get 'backup_artifact.delete_cancelled'() { return tx("Yedekleme silme talebi iptal edildi") },
  get 'backup_replica.physical_delete_completed'() { return tx("Yedekleme kopyası fiziksel olarak silindi") },
  get 'backup_run.attempt_completed'() { return tx("Yedekleme denemesi sonuçlandı") },
  get 'backup_schedule.occurrence_decided'() { return tx("Zamanlanmış çalıştırma kararı verildi") },
  get 'backup_artifact.retention_applied'() { return tx("Yedekleme saklama süresi doldu") },
  get 'backup_replica.physical_delete_failed'() { return tx("Yedekleme kopyası silinemedi") },
  get 'backup_replica.orphan_recorded'() { return tx("Sahipsiz yedekleme kopyası kaydedildi") },
  get 'backup_replica.quarantined'() { return tx("Yedekleme kopyası karantinaya alındı") },
  get 'backup_storage.orphan_delete_completed'() { return tx("Sahipsiz depolama nesnesi silindi") },
}

const actorLabels: Record<PlatformAuditEntry['actorKind'], string> = {
  get PlatformAdmin() { return tx("PlatformAdmin") },
  get TenantAdministrator() { return tx("Tenant yöneticisi") },
  get System() { return tx("Sistem") },
  get User() { return tx("Kullanıcı") },
  get Anonymous() { return tx("Anonim") },
}

function auditActionLabel(action: string) {
  return Object.hasOwn(actionLabels, action) ? actionLabels[action] : action
}

function detailValue(value: AuditDetailValue) {
  if (value === null) return '—'
  if (typeof value === 'boolean') return value ? tx('Evet') : tx('Hayır')
  return String(value)
}

function Details({ details }: { details: PlatformAuditEntry['details'] }) {
  useLanguage()
  const entries = Object.entries(details ?? {})
  if (entries.length === 0) return <span className="text-text-muted">—</span>
  return <dl className="space-y-0.5 text-xs">{entries.map(([key, value]) => <div key={key} className="flex flex-wrap gap-1"><dt className="text-text-muted">{key}:</dt><dd className="break-all">{detailValue(value)}</dd></div>)}</dl>
}

export function AuditTable({ rows, emptyMessage, showTenant = true, className }: { rows: readonly PlatformAuditEntry[]; emptyMessage: string; showTenant?: boolean; className?: string }) {
  useLanguage()
  const columns: Column<PlatformAuditEntry>[] = [
    { key: 'at', header: tx('Zaman'), render: (entry) => formatDateTime(entry.at, tx('Henüz bildirilmedi')) },
    { key: 'action', card: 'title', header: tx('İşlem'), wrap: true, render: (entry) => <span className="font-medium">{auditActionLabel(entry.action)}</span> },
    { key: 'actor', header: tx('Yapan'), wrap: true, render: (entry) => <div className="flex flex-wrap items-center gap-1.5"><span>{entry.actorDisplayName ?? tx('Bilinmeyen kullanıcı')}</span><Badge tone={entry.actorKind === 'PlatformAdmin' ? 'warning' : entry.actorKind === 'System' || entry.actorKind === 'Anonymous' ? 'neutral' : 'info'}>{actorLabels[entry.actorKind]}</Badge></div> },
    ...(showTenant ? [{ key: 'tenant', header: tx('Tenant'), wrap: true, render: (entry: PlatformAuditEntry) => entry.tenantId ? <Link className={linkClass} to={platformPaths.tenant(entry.tenantId)}>{entry.tenantName ?? 'Tenant'}</Link> : <span className="text-text-muted">—</span> }] : []),
    { key: 'target', card: 'hidden', header: tx('Hedef'), wrap: true, render: (entry) => entry.targetType },
    { key: 'details', card: 'hidden', header: tx('Ayrıntılar'), wrap: true, render: (entry) => <Details details={entry.details} /> },
  ]
  return <DataList rows={rows} columns={columns} keyExtractor={(entry) => entry.id} emptyMessage={emptyMessage} className={className} />
}
