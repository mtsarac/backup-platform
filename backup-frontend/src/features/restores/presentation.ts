import { tx } from '@/lib/i18n'
import { ApiError } from '@/lib/api/client'
import type { TechnicalItem } from '@/components/ui/technical-details'
import type { RestoreCleanup, RestoreMode, RestoreResult, RestoreStatus, RestoreTargetState } from './types'

export const restoreStatuses: readonly RestoreStatus[] = ['Pending', 'Leased', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'AgentLost']
export function isActiveRestore(status: string): boolean { return ['Pending', 'Leased', 'Running'].includes(status) }
export function canCancelRestore(status: string, cancellationRequestedAt?: string | null): boolean { return isActiveRestore(status) && !cancellationRequestedAt }
export function restoreModeLabel(mode: RestoreMode): string { return mode === 'NewDatabase' ? tx('Yeni Database') : tx('Mevcut Database üzerine yaz') }
export function targetStateLabel(state?: RestoreTargetState | null): string {
  return state ? ({ NotModified: tx('Değiştirilmedi'), Online: tx('Çevrimiçi'), PartiallyRestored: tx('Kısmen geri yüklendi'), Unknown: tx('Bilinmiyor') })[state] : tx('Henüz bildirilmedi')
}
export function targetStateDescription(state?: RestoreTargetState | null): string {
  switch (state) {
    case 'NotModified': return tx('Hedef veritabanı değiştirilmedi. Bu, veritabanının sağlığının kontrol edildiği anlamına gelmez.')
    case 'Online': return tx('Yedekleme bilgisayarı, geri yüklenen veritabanının çalışır durumda olduğunu bildirdi.')
    case 'PartiallyRestored': return tx('Hedef değiştirildi ama geri yükleme tamamlanmadı. Veritabanı hasarlı veya eksik olabilir; üzerine yazılan eski içerik kaybolmuş olabilir. Elle müdahale gerekebilir.')
    case 'Unknown': return tx('Yedekleme bilgisayarı veritabanının durumunu belirleyemedi. Veritabanını sağlıklı saymayın; elle kontrol edin.')
    default: return tx('Veritabanının durumu henüz bildirilmedi. İşlemin durumundan veritabanının sağlığı anlaşılamaz.')
  }
}
export function restoreStageLabel(stage?: string | null): string {
  const labels: Record<string, string> = {
    Preparing: tx('Hazırlanıyor'), DownloadingArtifact: tx('Artifact aktarılıyor'), VerifyingChecksum: tx('Checksum doğrulanıyor'),
    InspectingBackup: tx('Backup inceleniyor'), PreparingTarget: tx('Hedef hazırlanıyor'), Restoring: tx('Geri yükleniyor'),
    Finalizing: tx('Tamamlanıyor'), Verifying: tx('Geri yüklenen Database doğrulanıyor'), CleaningUp: tx('Geçici Database kaldırılıyor'), Completed: tx('Tamamlandı'), Failed: tx('Başarısız'), Cancelled: tx('İptal edildi'),
  }
  return stage ? labels[stage] ?? tx('Bildirilen aşama tanınmıyor') : tx('Henüz ilerleme bildirilmedi')
}
export function safeRestoreCode(code?: string | null): string { return code && /^[A-Za-z0-9_.-]{1,80}$/.test(code) ? code : '—' }
const databaseStates = ['ONLINE', 'RESTORING', 'RECOVERING', 'RECOVERY_PENDING', 'SUSPECT', 'EMERGENCY', 'OFFLINE', 'COPYING', 'OFFLINE_SECONDARY']
export function databaseStateLabel(state?: string | null): string {
  const key = state?.toUpperCase()
  return key && databaseStates.includes(key) ? tx(key) : tx('Bilinmiyor')
}
export function restoreProgressPercent(value?: number | null): number | undefined {
  return value != null && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : undefined
}
export function verificationCheckLabel(checkId: string): string {
  const labels: Record<string, string> = { 'database-online': tx('Database çevrimiçi'), integrity: tx('Bütünlük kontrolü'), 'data-readable': tx('Veri okunabilir') }
  return labels[checkId] ?? safeRestoreCode(checkId)
}
export function verificationCleanupText(cleanup: RestoreCleanup): string {
  if (cleanup.state === 'Completed') return tx('Geçici Database kaldırıldı')
  if (cleanup.needsAttention) return tx('Kaldırma dikkat gerektiriyor')
  return tx("Kaldırma bekliyor, otomatik yeniden denenecek (deneme {0})", { "0": cleanup.attempts })
}
/** What a pre-check is about, in everyday words. Ids come from the engines and are an open set, so unknown ones get a generic name; the raw id lives in the technical details. */
export function precheckLabel(checkId: string): string {
  const labels: Record<string, string> = {
    'server.reachable': tx('Sunucuya ulaşılabiliyor'), 'server.authentication': tx('Sunucu girişi kabul ediliyor'), 'server.instance': tx('Doğru sunucu örneğine bağlanılıyor'),
    'database.exists': tx('Hedef veritabanı durumu'), 'database.state': tx('Veritabanı çalışır durumda'), 'permission.restore': tx('Geri yükleme izni var'),
    'restore.target-server': tx('Hedef sunucu bilgisi okunabiliyor'), 'restore.database-name': tx('Veritabanı adı uygun'), 'restore.target-protected': tx('Hedef korunan bir veritabanı değil'),
    'restore.target-sessions': tx('Hedefte açık bağlantı yok'), 'restore.target-directories': tx('Hedef klasörler uygun'), 'restore.disk-space': tx('Yeterli disk alanı var'),
    'restore.backup-type': tx('Yedek türü destekleniyor'), 'restore.version-compatibility': tx('Sunucu sürümleri uyumlu'), 'restore.edition-compatibility': tx('Sunucu sürüm türü uyumlu'),
    'restore.target-relation': tx('Hedef, yedeğin kaynağıyla uyumlu'), 'restore.file-types': tx('Dosya türleri destekleniyor'), 'restore.file-mapping': tx('Dosya yerleşimi hazır'),
    'staging.agent-access': tx('Geçici klasöre yazılabiliyor'), 'staging.disk-space': tx('Geçici klasörde yeterli alan var'), 'staging.path-format': tx('Geçici klasör yolu geçerli'),
    'staging.server-writable': tx('Sunucu geçici klasöre yazabiliyor'), 'staging.mapping': tx('Geçici klasör sunucuyla paylaşılıyor'),
    'source.key': tx('Yedek anahtarı bu çalışma alanına ait'), 'source.storage': tx('Yedeğin saklandığı depolama okunabiliyor'), 'source.replica': tx('Yedek kopyası mevcut'),
    'file-system.source': tx('Dosya kaynağı kullanılabilir'),
    'postgres.tools': tx('pg_restore aracı hazır'), 'postgres.restore': tx('PostgreSQL geri yükleme ön kontrolü'), 'postgres.restore-plan': tx('Geri yükleme planı hazırlanabiliyor'),
    'postgres.restore-permission': tx('Geri yükleme izni var'), 'postgres.target': tx('Hedef veritabanı seçilen moda uygun'), 'postgres.archive-compatibility': tx('Yedek arşivi hedef sunucuyla uyumlu'),
    'postgres.verification-scope': tx('Doğrulama kapsamı'), 'postgres.archive-trust': tx('Yedek arşivinin kaynağı güvenilir olmalı'), 'postgres.roles': tx('Rol tanımları hedefte hazır olmalı'),
  }
  return Object.hasOwn(labels, checkId) ? labels[checkId] : tx('Diğer kontrol')
}
/** Warning texts come from the engines in English; known reason codes get Turkish wording, unknown ones keep the engine's message. */
export function precheckWarningText(item: { reasonCode: string; message: string }): string {
  const texts: Record<string, string> = {
    'role-definitions-not-in-archive': tx('Rol tanımları yedeğe dahil değildir. Nesne sahibi ve ACL rollerinin hedef sunucuda önceden bulunması gerekir; eksik rol varsa geri yükleme yayımlanmadan önce güvenli biçimde başarısız olur. Sahiplik ve ACL bilgileri sessizce atılmaz.'),
    'trusted-archive-required': tx('Yalnızca güvenilir platform yedeklerini geri yükleyin: arşivdeki şema SQL’i, işlevler ve eklentiler geri yükleme girişinin yetkileriyle çalışır.'),
    'ownership-not-preserved': tx('Doğrulama geri yüklemesi nesne sahiplerini, ACL’leri ve veritabanı ayarlarını bilerek korumaz; yalnızca süper kullanıcının oluşturabildiği eklentiler geri yüklemeyi hemen başarısız kılar.'),
    'staging-space-unknown': tx('Geçici klasörün boş alanı belirlenemedi.'),
    'server-version-unknown': tx('Hedef sunucunun SQL Server sürümü okunamadı; sürüm uyumluluğu doğrulanamıyor.'),
    'target-protection-unknown': tx('SQL Server hedef veritabanının kullanılabilirlik grubu veya yansıtma oturumuna ait olup olmadığını bildirmedi; geri yükleme sırasında anlaşılacak.'),
    'database-single-user': tx('Veritabanı SINGLE_USER modunda; tek bağlantıyı başka bir oturum tutuyorsa geri yükleme başarısız olur.'),
    'database-restricted-user': tx('Veritabanı RESTRICTED_USER modunda.'),
    'database-restoring': tx('Veritabanı RESTORING durumunda (örneğin yarım kalmış bir geri yüklemeden sonra); üzerine yazılacak.'),
    'database-offline': tx('Veritabanı çevrimdışı; üzerine yazılacak.'),
    'database-recovery-pending': tx('Veritabanı kurtarma bekliyor; üzerine yazılacak.'),
    'database-suspect': tx('Veritabanı şüpheli olarak işaretli; üzerine yazılacak.'),
    'database-emergency': tx('Veritabanı acil durum modunda; üzerine yazılacak.'),
    'sessions-unknown': tx('SQL Server açık oturumları bildirmedi (VIEW SERVER STATE gerekir); oturum varsa geri yükleme başarısız olabilir veya oturumlar kesilebilir.'),
    'sessions-will-be-terminated': tx('Veritabanına bağlı başka oturumlar var; bağlantıları kesilecek ve açık işlemleri geri alınacak.'),
    'restore-permission-unknown': tx('SQL Server girişin bu veritabanını geri yükleyip yükleyemeyeceğini bildirmedi; geri yükleme sırasında anlaşılacak.'),
    'target-directories-unknown': tx('SQL Server varsayılan veri ve günlük klasörlerini bildirmedi; yedek planlanırken belirlenecek.'),
    'target-directories-fallback': tx('SQL Server varsayılan klasörlerini bildirmiyor; master veritabanı dosyalarının klasörü kullanılacak.'),
    'disk-space-unknown': tx('SQL Server veri biriminin boş alanını bildirmedi (VIEW SERVER STATE gerekir); alan denetimi yedek planlanırken yinelenir.'),
    'backup-version-unknown': tx('Yedeğin veya hedefin SQL Server sürümü okunamadı; sürüm uyumluluğu doğrulanamıyor.'),
    'backup-from-newer-build': tx('Yedek, hedefin çalıştırdığından daha yeni bir SQL Server yapısından geliyor; veritabanı o güncellemeyle gelen bir özelliği kullanıyorsa geri yükleme başarısız olabilir.'),
    'backup-version-old': tx('Yedek SQL Server 2008’den eski bir sürümle alınmış; hedef onu geri yükleyemeyebilir.'),
    'edition-compatibility-unknown': tx('Hedef sürüm, yedeğin kullanabileceği tüm Enterprise özelliklerini desteklemiyor; SQL Server bunu geri yükleme sırasında denetler, önceden doğrulanamaz.'),
    'target-relation-unknown': tx('Mevcut veritabanının bu yedeğin veritabanından oluşturulup oluşturulmadığı belirlenemedi.'),
    'target-unrelated-to-backup': tx('Mevcut veritabanı yedeklenen veritabanıyla ilişkili değil (farklı aile GUID’i); üzerine yazma onaylandığı için değiştirilecek.'),
  }
  return Object.hasOwn(texts, item.reasonCode) ? texts[item.reasonCode] : item.message
}
/**
 * How a failed start reads to the user. A failure that is not an API answer leaves it unknown whether the restore began; retrying is safe
 * because the same request key is reused.
 */
export function describeStartFailure(error: unknown) {
  if (error == null || error instanceof ApiError) return error ?? undefined
  return {
    what: tx('Geri yükleme başlatılamadı.'),
    dataImpact: tx('Geri yüklemenin başlayıp başlamadığını geri yükleme listesinden kontrol edin.'),
    action: tx('Aynı işlemi tekrar deneyin; yeniden denemeler ikinci bir geri yükleme oluşturmaz.'),
  }
}
/** The engines' own wording and codes for each check; the table above says the same thing in everyday words. */
export function technicalChecks(items: RestoreResult['preCheck']): TechnicalItem[] {
  const seen = new Map<string, number>()
  return items.map((item) => {
    const count = (seen.get(item.checkId) ?? 0) + 1
    seen.set(item.checkId, count)
    return { label: count > 1 ? `${item.checkId} (${count})` : item.checkId, value: [item.status, safeRestoreCode(item.reasonCode), item.message].filter(Boolean).join(' · ') }
  })
}
