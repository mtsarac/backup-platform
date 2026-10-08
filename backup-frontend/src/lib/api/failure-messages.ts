import { ApiError } from './client'

const messages: Record<string, string> = {
  DatabaseUnavailable: 'Database bağlantısı geçici olarak kullanılamıyor.',
  DatabaseNotFound: 'Database bulunamadı. Kaynak yapılandırmasını kontrol edin.',
  not_cancellable: 'Silme iptal edilemez; fiziksel silme başlamış veya bekleyen bir istek yok.',
  already_deleted: 'Bu backup dosyalarının tüm replikaları zaten silindi.',
  agent_unavailable: "Agent kullanılamıyor. Agent'ın tekrar bağlanması bekleniyor.",
  storage_unavailable: 'Storage geçici olarak kullanılamıyor.',
  access_denied: 'Storage erişim izni reddedildi.',
  io_error: 'Storage dosya işlemi tamamlanamadı.',
  verification_failed: 'Fiziksel dosyanın silindiği doğrulanamadı.',
  PermissionDenied: 'İşlem için gerekli izinler bulunmuyor.',
  AuthenticationFailed: 'Database kimlik doğrulaması başarısız oldu. Credential referansını kontrol edin.',
  StagingInvalid: 'Staging dizini veya SQL Server dosya eşlemesi geçersiz.',
  AgentLost: 'Agent bağlantısı kaybedildi; bu attempt tamamlanamadı.',
  ScheduleGraceExpired: 'Zamanlanan backup için tolerans süresi doldu.',
  storage_not_configured: 'Storage yapılandırılmamış.',
  StorageUnavailable: 'Storage geçici olarak kullanılamıyor.',
  StorageWriteFailed: 'Backup storage alanına yazılamadı.',
  ReplicaVerificationFailed: 'Storage kopyası doğrulanamadı.',
  VerificationFailed: 'Backup doğrulaması başarısız oldu.',
  HashFailed: 'Checksum hesaplanamadı.',
  SqlBackupFailed: 'SQL Server backup işlemi başarısız oldu.',
  PreCheckFailed: 'Backup ön kontrolleri başarısız oldu.',
  InvalidConfiguration: 'Backup yapılandırması geçersiz.',
  UnsupportedCapability: 'Agent veya SQL Server istenen özelliği desteklemiyor.',
  OperationTimedOut: 'İşlem zaman aşımına uğradı.',
  InternalError: 'İşlem tamamlanamadı. Sistem yöneticisiyle iletişime geçin.',
  Cancelled: 'İşlem iptal edildi.',
  overlap_skipped: 'Backup zaten çalışıyor.',
  job_overlap: 'Bu Job için başka bir backup çalışıyor.',
  attempts_exhausted: 'İzin verilen toplam attempt sayısına ulaşıldı.',
  succeeded: 'Başarılı backup tekrar denenemez.',
  cancelled: 'İptal edilmiş backup tekrar denenemez.',
  in_progress: 'Bir attempt zaten sırada veya çalışıyor.',
  invalid_request: 'Girilen değer geçersiz. Yapılandırmayı kontrol edin.',
  invalid_cron: 'Cron ifadesi geçersiz. Dakika saat gün ay haftanın-günü formatını kullanın.',
  invalid_time_zone: 'Geçerli bir IANA timezone girin.',
  invalid_timezone: 'Geçerli bir IANA timezone girin.',
  invalid_grace_period: 'Missed-run grace süresi geçersiz.',
  invalid_grace: 'Missed-run grace süresi geçersiz.',
  delete_started: 'Fiziksel silme başladığı için silme iptal edilemez.',
  deletion_in_progress: 'Fiziksel silme başladı; artık iptal edilemez.',
  administrator_required: 'Saklama politikasını yalnızca Owner / Admin değiştirebilir.',
  conflicting_policy: 'Zamanlama ayarları birbiriyle çakışıyor. Alanları kontrol edin.',
  already_member: 'Bu kullanıcı zaten tenant üyesi.',
  user_not_found: 'Kullanıcı bulunamadı.',
  tenant_not_found: 'Tenant bulunamadı.',
  membership_not_found: 'Tenant üyeliği bulunamadı. Liste güncel olmayabilir.',
  login_taken: 'Bu kullanıcı adı zaten kullanılıyor.',
  agent_not_found: 'Agent bulunamadı.',
  enrollment_disabled: 'Yeni Agent kaydı kapalı. Agent kaydı sayfasından kayıt iznini açın.',
  invalid_enrollment_token: 'Kayıt token’ı geçersiz.',
  enrollment_token_expired: 'Kayıt token’ının süresi doldu. Yeni bir token oluşturun.',
  enrollment_token_consumed: 'Kayıt token’ı zaten kullanılmış.',
  token_revoked: 'Kayıt token’ı iptal edilmiş.',
  invalid_value: 'Girilen değer geçersiz.',
  required: 'Zorunlu bir alan boş bırakıldı.',
  too_long: 'Girilen değer çok uzun.',
  invalid_length: 'Girilen değerin uzunluğu geçersiz.',
  plan_not_found: 'Plan bulunamadı. Liste güncel olmayabilir.',
  plan_name_taken: 'Bu plan adı zaten kullanılıyor.',
  system_plan_immutable: 'Sistem planı değiştirilemez.',
  concurrent_update: 'Plan başka bir işlemle aynı anda değiştirildi. Sayfayı yenileyip tekrar deneyin.',
  plan_disabled: 'Bu plan devre dışı; yeni atamalarda seçilemez.',
  plan_already_assigned: 'Tenant zaten bu planı kullanıyor.',
  subscription_not_found: 'Tenant için abonelik bulunamadı.',
  subscription_not_active: 'Abonelik etkin değil; askıya alınamaz.',
  subscription_not_suspended: 'Abonelik askıda değil; yeniden etkinleştirilemez.',
  subscription_not_open: 'Abonelik sona ermiş; yalnızca yeni bir plan atanabilir.',
  subscription_expired: 'Abonelik süresi dolmuş; yalnızca yeni bir plan atanabilir.',
  invalid_kind: 'Limit türü geçersiz.',
  unexpected: 'Bu limit türü için bayt değeri girilemez.',
  out_of_range: 'Girilen değer izin verilen aralığın dışında.',
  ambiguous: 'Sınırlı boyut ile limitsiz seçeneği birlikte kullanılamaz.',
  invalid_characters: 'Girilen değer geçersiz karakterler içeriyor.',
  platform_admin_required: 'Platform Administration için PlatformAdmin yetkisi gerekir.',
}
// Codes whose text must win over the generic 403/404 wording.
const statusSpecificCodes: Record<string, true> = { tenant_not_found: true, user_not_found: true, membership_not_found: true, agent_not_found: true, plan_not_found: true, subscription_not_found: true, platform_admin_required: true, enrollment_disabled: true }
export function failureMessage(code?: string | null): string {
  if (!code) return 'İşlem başarısız oldu.'
  return messages[code] ?? (/^[A-Za-z0-9_.-]{1,80}$/.test(code) ? `İşlem başarısız oldu (${code}).` : 'İşlem başarısız oldu.')
}
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 401) return 'Oturumunuz sona erdi. Yeniden giriş yapın.'
    const code = error.reason ?? error.code
    if ((error.status === 403 || error.status === 404) && code && Object.hasOwn(statusSpecificCodes, code)) return messages[code]
    if (error.status === 403) return 'Bu işlem için yetkiniz yok.'
    if (error.status === 404) return 'Kayıt bulunamadı veya bu tenant altında erişilemiyor.'
    if (error.status >= 500) return 'Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.'
    return failureMessage(code)
  }
  return 'Sunucuya ulaşılamadı. Bağlantınızı kontrol edip tekrar deneyin.'
}
