import { tx } from '@/lib/i18n'
import { ApiError } from './client'
import type { TechnicalItem } from '@/components/ui/technical-details'

const messages: Record<string, string> = {
  get DirectPathUnavailable() { return tx('DirectPathUnavailable: Doğrudan Agent bağlantısı veya güvenli diske yazma desteği kullanılamıyor. Backend aktarımına geçilmez.') },
  get replica_unavailable() { return tx("Replica şu anda indirme için kullanılamıyor.") },
  get replica_not_found() { return tx("Replica bulunamadı veya bu Backup altında erişilemiyor.") },
  get storage_read_not_supported() { return tx("Storage Provider güvenli indirmeyi desteklemiyor.") },
  get integrity_mismatch() { return tx("Dosya checksum doğrulaması başarısız. İndirme tamamlanmadı.") },
  get download_expired() { return tx("İndirme oturumunun süresi doldu. Yeni bir indirme başlatın.") },
  get download_interrupted() { return tx("İndirme kesildi. Yeni bir indirme başlatın.") },
  get transfer_failed() { return tx("Dosya aktarımı başarısız oldu. Yeni bir indirme başlatın.") },
  get DatabaseUnavailable() { return tx("Database bağlantısı geçici olarak kullanılamıyor.") },
  get DatabaseNotFound() { return tx("Database bulunamadı. Kaynak yapılandırmasını kontrol edin.") },
  get not_cancellable() { return tx("Silme iptal edilemez; fiziksel silme başlamış veya bekleyen bir istek yok.") },
  get already_deleted() { return tx("Bu backup dosyalarının tüm replikaları zaten silindi.") },
  get agent_unavailable() { return tx("Agent kullanılamıyor. Agent'ın tekrar bağlanması bekleniyor.") },
  get storage_unavailable() { return tx("Storage geçici olarak kullanılamıyor.") },
  get access_denied() { return tx("Storage erişim izni reddedildi.") },
  get io_error() { return tx("Storage dosya işlemi tamamlanamadı.") },
  get verification_failed() { return tx("Fiziksel dosyanın silindiği doğrulanamadı.") },
  get PermissionDenied() { return tx("İşlem için gerekli izinler bulunmuyor.") },
  get AuthenticationFailed() { return tx("Database kimlik doğrulaması başarısız oldu. Credential referansını kontrol edin.") },
  get StagingInvalid() { return tx("Staging dizini veya SQL Server dosya eşlemesi geçersiz.") },
  get AgentLost() { return tx("Agent bağlantısı kaybedildi; bu attempt tamamlanamadı.") },
  get ScheduleGraceExpired() { return tx("Zamanlanan backup için tolerans süresi doldu.") },
  get storage_not_configured() { return tx("Storage yapılandırılmamış.") },
  get StorageUnavailable() { return tx("Storage geçici olarak kullanılamıyor.") },
  get StorageWriteFailed() { return tx("Backup storage alanına yazılamadı.") },
  get QuotaExceeded() { return tx("Gerçek backup boyutu kesin depolama kotasında kullanılabilir alanı aşıyor; hedefe yükleme reddedildi.") },
  get ReplicaVerificationFailed() { return tx("Storage kopyası doğrulanamadı.") },
  get VerificationFailed() { return tx("Backup doğrulaması başarısız oldu.") },
  get HashFailed() { return tx("Checksum hesaplanamadı.") },
  get SqlBackupFailed() { return tx("SQL Server backup işlemi başarısız oldu.") },
  get PreCheckFailed() { return tx("Backup ön kontrolleri başarısız oldu.") },
  get InvalidConfiguration() { return tx("Backup yapılandırması geçersiz.") },
  get UnsupportedCapability() { return tx("Agent veya SQL Server istenen özelliği desteklemiyor.") },
  get OperationTimedOut() { return tx("İşlem zaman aşımına uğradı.") },
  get InternalError() { return tx("İşlem tamamlanamadı. Sistem yöneticisiyle iletişime geçin.") },
  get Cancelled() { return tx("İşlem iptal edildi.") },
  get overlap_skipped() { return tx("Backup zaten çalışıyor.") },
  get quota_exceeded() { return tx("Depolama kotası doldu; yeni backup başlatılamadı. Mevcut backup'lar silinmedi ve geri yüklenebilir.") },
  get quota_size_unknown() { return tx("Backup boyutu henüz bilinmediği için kesin depolama kotasında güvenli alan ayrılamadı; yeni backup başlatılmadı. Boyut tahmininin belirlenmesi için yöneticiye başvurun.") },
  get quota_bounded_upload_unsupported() { return tx("Agent, yükleme öncesinde kesin depolama kotasını uygulamayı desteklemiyor; Agent'ı güncelleyin. Yeni backup başlatılmadı.") },
  get subscription_required() { return tx("Etkin bir abonelik bulunmadığı için yeni backup başlatılamadı.") },
  get storage_connection_disabled() { return tx("Hedef depolama bağlantısı devre dışı; yeni backup başlatılamaz.") },
  get storage_connection_unavailable() { return tx("Hedef depolama bağlantısı bulunamadı veya bu Job'un Storage Provider'ına ait değil; yeni backup başlatılamaz.") },
  get multi_destination_execution_not_supported() { return tx("Bu zamanlanmış çalışma, çoklu hedefli yedekleme desteklenmeden önce kaydedilmişti; çalıştırma başlatılmadı ve otomatik olarak yeniden denenmez.") },
  get agent_multi_destination_unsupported() { return tx("İşin Agent'ı çoklu hedefli yedekleme desteği bildirmedi: Agent eski olabilir veya bağlantısını henüz tamamlamamış olabilir (yeni Agent ilk heartbeat'inden birkaç saniye sonra bildirir). Kısa süre sonra yeniden deneyin ya da Agent'ı güncelleyin. Yeni backup başlatılamadı.") },
  get agent_direct_storage_unsupported() { return tx("Bu SFTP/Google Drive hedefi eski relay modunda yapılandırılmış veya Agent doğrudan depolama desteği bildirmedi. Relay kapatıldı: Agent'ı güncelleyin ve hedefi doğrudan aktarım için yeniden kaydedin. Mevcut yedekler etkilenmedi; yeni backup başlatılamadı.") },
  get agent_engine_unsupported() { return tx('Agent seçilen yedekleme motorunu desteklemiyor. Agent yeteneklerini kontrol edin veya Agent’ı güncelleyin.') },
  get destination_combination_unsupported() { return tx("İşin hedefleri birlikte çalıştırılamıyor: aynı depolama bağlantısı birden fazla kez kullanılıyor ya da İşin Agent'ı bir hedef türünü veya 3-2-1 denetimini desteklemiyor. Yeni backup başlatılamadı.") },
  get three_two_one_unsatisfied() { return tx("İş 3-2-1 kuralını zorunlu kılıyor ancak hedefleri iki farklı ortam türünü ve uzak konumdaki bir kopyayı kapsamıyor. Depolama bağlantılarının ortam ve konum ayarlarını ile işin kaynak konumunu kontrol edin. Yeni backup başlatılamadı.") },
  get storage_connection_not_found() { return tx("Depolama bağlantısı bulunamadı. Liste güncel olmayabilir.") },
  get storage_connection_name_taken() { return tx("Bu ada sahip bir depolama bağlantısı zaten var.") },
  get administratively_disabled() { return tx("Bağlantı platform tarafından kilitlendi. Tenant tercihi bu kilidi kaldıramaz; platform yöneticisine başvurun.") },
  get connection_limit_reached() { return tx("Bu Storage Provider için bağlantı sınırına ulaşıldı.") },
  get platform_managed() { return tx("Platform yönetimli bağlantıyı yalnızca platform yöneticisi değiştirebilir.") },
  get tenant_managed() { return tx("Tenant yönetimli bağlantı için bu işlemi yalnızca tenant yöneticisi yapabilir.") },
  get platform_managed_provider() { return tx("Bu Storage Provider yalnızca platform tarafından yönetilir; tenant bağlantı ekleyemez.") },
  get connection_in_use() { return tx("Bağlantı hâlâ Replica veya Backup Job tarafından kullanılıyor; silinemez.") },
  get provider_not_available() { return tx("Bu Storage Provider henüz kullanılamıyor.") },
  get secrets_not_supported() { return tx("Bu Storage Provider kimlik bilgisi kabul etmiyor.") },
  get unknown_field() { return tx("Bu Storage Provider için tanımlı olmayan bir alan gönderildi.") },
  get job_overlap() { return tx("Bu Job için başka bir backup çalışıyor.") },
  get attempts_exhausted() { return tx("İzin verilen toplam attempt sayısına ulaşıldı.") },
  get succeeded() { return tx("Başarılı backup tekrar denenemez.") },
  get cancelled() { return tx("İptal edilmiş backup tekrar denenemez.") },
  get SourceInaccessible() { return tx("Kaynak replika okunamadı veya erişilemedi.") },
  get SourceChecksumMismatch() { return tx("Kaynak replika dosya bütünlüğü (SHA-256) uyuşmadı.") },
  get DestinationReadbackFailed() { return tx("Hedef depolama yazma sonrası doğrulama başarısız oldu.") },
  get NoVerifiedSourceAvailable() { return tx("Onarım için doğrulanmış ve erişilebilir kaynak replika bulunamadı.") },
  get UnsupportedSource() { return tx("Seçilen kaynak depolama türü arka plandan aktarımı desteklemiyor.") },
  get AlreadyVerified() { return tx("Bu hedefte zaten doğrulanmış bir replika mevcut.") },
  get TargetConflict() { return tx("Hedefte beklenen içerikle uyuşmayan bir dosya var. Sahipliği doğrulanmadan silinmez; inceleme için karantinaya alındı.") },
  get ArtifactExpired() { return tx("Yedekleme saklama süresi dolduğu için onarım başlatılamaz.") },
  get repair_already_in_progress() { return tx("Bu hedef için devam eden bir onarım işlemi zaten var.") },
  get unauthorized_destination() { return tx("Hedef depolama, bu yedeğin yapılandırma hedefleri arasında yer almıyor.") },
  get in_progress() { return tx("Bir attempt zaten sırada veya çalışıyor.") },
  get invalid_request() { return tx("Girilen değer geçersiz. Yapılandırmayı kontrol edin.") },
  get invalid_cron() { return tx("Cron ifadesi geçersiz. Dakika saat gün ay haftanın-günü formatını kullanın.") },
  get invalid_time_zone() { return tx("Geçerli bir IANA timezone girin.") },
  get invalid_timezone() { return tx("Geçerli bir IANA timezone girin.") },
  get invalid_grace_period() { return tx("Missed-run grace süresi geçersiz.") },
  get invalid_grace() { return tx("Missed-run grace süresi geçersiz.") },
  get delete_started() { return tx("Fiziksel silme başladığı için silme iptal edilemez.") },
  get deletion_in_progress() { return tx("Fiziksel silme başladı; artık iptal edilemez.") },
  get administrator_required() { return tx("Saklama politikasını yalnızca Owner / Admin değiştirebilir.") },
  get conflicting_policy() { return tx("Zamanlama ayarları birbiriyle çakışıyor. Alanları kontrol edin.") },
  get already_member() { return tx("Bu kullanıcı zaten tenant üyesi.") },
  get user_not_found() { return tx("Kullanıcı bulunamadı.") },
  get tenant_not_found() { return tx("Tenant bulunamadı.") },
  get membership_not_found() { return tx("Tenant üyeliği bulunamadı. Liste güncel olmayabilir.") },
  get login_taken() { return tx("Bu kullanıcı adı zaten kullanılıyor.") },
  get agent_not_found() { return tx("Agent bulunamadı.") },
  get enrollment_disabled() { return tx("Yeni Agent kaydı kapalı. Agent kaydı sayfasından kayıt iznini açın.") },
  get invalid_enrollment_token() { return tx("Kayıt token’ı geçersiz.") },
  get enrollment_token_expired() { return tx("Kayıt token’ının süresi doldu. Yeni bir token oluşturun.") },
  get enrollment_token_consumed() { return tx("Kayıt token’ı zaten kullanılmış.") },
  get token_revoked() { return tx("Kayıt token’ı iptal edilmiş.") },
  get invalid_value() { return tx("Girilen değer geçersiz.") },
  get required() { return tx("Zorunlu bir alan boş bırakıldı.") },
  get too_long() { return tx("Girilen değer çok uzun.") },
  get invalid_length() { return tx("Girilen değerin uzunluğu geçersiz.") },
  get plan_not_found() { return tx("Plan bulunamadı. Liste güncel olmayabilir.") },
  get plan_name_taken() { return tx("Bu plan adı zaten kullanılıyor.") },
  get system_plan_immutable() { return tx("Sistem planı değiştirilemez.") },
  get concurrent_update() { return tx("Plan başka bir işlemle aynı anda değiştirildi. Sayfayı yenileyip tekrar deneyin.") },
  get plan_disabled() { return tx("Bu plan devre dışı; yeni atamalarda seçilemez.") },
  get plan_already_assigned() { return tx("Tenant zaten bu planı kullanıyor.") },
  get subscription_not_found() { return tx("Tenant için abonelik bulunamadı.") },
  get subscription_not_active() { return tx("Abonelik etkin değil; askıya alınamaz.") },
  get subscription_not_suspended() { return tx("Abonelik askıda değil; yeniden etkinleştirilemez.") },
  get subscription_not_open() { return tx("Abonelik sona ermiş; yalnızca yeni bir plan atanabilir.") },
  get subscription_expired() { return tx("Abonelik süresi dolmuş; yalnızca yeni bir plan atanabilir.") },
  get invalid_kind() { return tx("Limit türü geçersiz.") },
  get unexpected() { return tx("Bu limit türü için bayt değeri girilemez.") },
  get out_of_range() { return tx("Girilen değer izin verilen aralığın dışında.") },
  get ambiguous() { return tx("Sınırlı boyut ile limitsiz seçeneği birlikte kullanılamaz.") },
  get invalid_characters() { return tx("Girilen değer geçersiz karakterler içeriyor.") },
  get platform_admin_required() { return tx("Platform Administration için PlatformAdmin yetkisi gerekir.") },
  get quota_policy_not_found() { return tx("Kota politikası bulunamadı.") },
  get quota_policy_key_taken() { return tx("Bu kota politikası anahtarı zaten kullanılıyor.") },
  get quota_policy_disabled() { return tx("Bu kota politikası devre dışı; plana atanamaz.") },
  get default_policy_required() { return tx("Varsayılan kota politikası devre dışı bırakılamaz.") },
  get unsupported_scope_kind() { return tx("Bu kapsam türü henüz desteklenmiyor.") },
  get support_access_denied() { return tx("Bu destek oturumu üzerinden erişim reddedildi.") },
  get session_expired() { return tx("Destek oturumunun süresi doldu.") },
  get session_closed() { return tx("Destek oturumu kapatıldı.") },
  get session_revoked() { return tx("Destek erişimi geri alındı.") },
  get permission_missing() { return tx("Destek oturumunda bu işlem için gereken izin yok.") },
  get not_session_owner() { return tx("Yalnızca destek oturumunu açan PlatformAdmin müşteri verilerine erişebilir ve oturumu kapatabilir.") },
  get replace_forbidden() { return tx("Destek erişimi yalnızca yeni Database geri yüklemeye izin verir; mevcut Database üzerine yazılamaz.") },
  get active_session_exists() { return tx("Bu tenant için zaten etkin bir destek oturumunuz var.") },
  get support_unavailable() { return tx("Destek erişimi güvenli audit kaydı oluşturulamadığı için kullanılamıyor. Hiçbir erişim açılmadı.") },
  get support_session_not_found() { return tx("Destek oturumu bulunamadı.") },
  get session_not_active() { return tx("Destek oturumu artık etkin değil.") },
  get idempotency_conflict() { return tx("Bu istek anahtarı farklı bir işlem için kullanıldı. Formu kapatıp yeniden açın.") },
  get view_required() { return tx("Download ve Restore izinleri Backup metadata görüntüleme iznini gerektirir.") },
  get notification_channel_not_found() { return tx("Bildirim kanalı bulunamadı. Liste güncel olmayabilir.") },
  get notification_channel_name_taken() { return tx("Bu ada sahip bir bildirim kanalı zaten var.") },
  get notification_channel_target_taken() { return tx("Bu hedef (adres, URL veya numara) için zaten bir bildirim kanalı var.") },
  get notification_channel_limit_reached() { return tx("Bildirim kanalı sınırına ulaşıldı. Kullanılmayan bir kanalı silin.") },
  get notification_test_rate_limited() { return tx("Bu kanal için saatlik test sınırına (5) ulaşıldı. Daha sonra tekrar deneyin.") },
  get discovery_unsupported() { return tx("Agent sürümü keşfi desteklemiyor. Agent'ı güncelleyin.") },
  get discovery_disabled() { return tx("Bu Agent için veritabanı keşfi kapalı. Ayarlardan etkinleştirin.") },
  get rescan_too_soon() { return tx("Az önce tarama istendi. Birkaç saniye sonra tekrar deneyin.") },
  get credentials_required() { return tx("Önce bu Instance için bir kimlik bilgisi referansı kaydedin.") },
  get scan_superseded() { return tx("Tarama sonucu daha yeni bir tarama tarafından değiştirildi. Sayfayı yenileyin.") },
  get transport_not_configured() { return tx("Bu kanal türü için platform aktarımı yapılandırılmamış. Platform yöneticisine başvurun.") },
  get EncryptionRequired() { return tx("İş şifreleme gerektiriyor ama yedek dosyası şifrelenmeden geldi; dosya yayımlanmadı.") },
  get artifact_not_encrypted() { return tx("Bu yedek dosyası şifreli değil; şifre çözme veya acil destek erişimi uygulanamaz.") },
  get key_unavailable() { return tx("Şifreleme anahtarı kullanılamıyor (emekli veya eksik). Dosya yalnızca Platform kurtarma anahtarı ile çevrimdışı çözülebilir.") },
  get decrypt_not_permitted() { return tx("Destek oturumu üzerinden şifre çözülemez. Gerekirse acil destek erişimi isteği açın.") },
  get artifact_not_found() { return tx("Backup bulunamadı veya bu tenant'a ait değil.") },
  get key_not_found() { return tx("Anahtar sürümü bulunamadı.") },
  get cannot_retire_active_key() { return tx("Etkin anahtar emekli edilemez. Önce anahtarı döndürün.") },
  get already_retired() { return tx("Anahtar zaten emekli edilmiş.") },
  get self_approval_forbidden() { return tx("İsteği açan yönetici kendi isteğini onaylayamaz veya reddedemez; farklı bir PlatformAdmin gerekir.") },
  get request_not_pending() { return tx("İstek artık onay beklemiyor; zaten karara bağlanmış veya kullanılmış.") },
  get request_expired() { return tx("İsteğin süresi doldu. Yeni bir istek açın.") },
  get request_not_approved() { return tx("İstek onaylanmadığı için indirilemez.") },
  get request_consumed() { return tx("Onaylanan istek zaten kullanıldı. Tek kullanımlıktır; yeni bir istek açın.") },
  get not_requester() { return tx("Yalnızca isteği açan yönetici indirebilir.") },
  get not_a_public_key() { return tx("Geçerli bir genel anahtar (PUBLIC KEY, SubjectPublicKeyInfo PEM) girin.") },
  get private_key_rejected() { return tx("Özel anahtar kabul edilmez. Yalnızca genel anahtarı girin; özel anahtar sizde kalmalıdır.") },
  get not_rsa() { return tx("Yalnızca RSA genel anahtarları desteklenir.") },
  get too_weak() { return tx("RSA anahtarı en az 4096 bit olmalıdır.") },
}
// Wording that differs from the shared table where the failure happens (restore) or where a shared code means something else (connections).
export type ErrorScope = 'restore' | 'storageConnection'
const scopedMessages: Record<ErrorScope, Record<string, string>> = {
  restore: {
  get ReplicaUnavailable() { return tx("Replica geri yükleme için kullanılamıyor.") }, get ArtifactTransferFailed() { return tx("Artifact aktarımı tamamlanamadı.") },
  get ArtifactKeyUnavailable() { return tx("Yedek şifreli ve şifreleme anahtarı kullanılamıyor (anahtar emekli edilmiş olabilir); yedek çözülemedi, geri yükleme başlatılmadı.") },
  get ChecksumMismatch() { return tx("Artifact checksum değeri eşleşmedi.") }, get BackupInvalid() { return tx("Backup doğrulaması başarısız oldu.") },
  get BackupTypeUnsupported() { return tx("Bu backup türü geri yükleme için desteklenmiyor.") }, get IncompatibleVersion() { return tx("Kaynak ve hedef Database motoru sürümleri uyumlu değil.") },
  get FileMappingFailed() { return tx("Database dosya eşlemesi oluşturulamadı.") }, get PreCheckFailed() { return tx("Geri yükleme ön kontrolleri başarısız oldu.") },
  get TargetDatabaseExists() { return tx("Hedef Database zaten var. Yeni Database modu üzerine yazmaz.") }, get TargetDatabaseNotFound() { return tx("Üzerine yazılacak hedef Database bulunamadı.") },
  get TargetInUse() { return tx("Hedef Database kullanımda.") }, get TargetProtected() { return tx("Korunan hedef Database değiştirilemez.") },
  get InsufficientDiskSpace() { return tx("Hedefte yeterli disk alanı yok.") }, get SqlRestoreFailed() { return tx("Database geri yükleme işlemi tamamlanamadı.") },
  get VerificationFailed() { return tx("Backup geri yüklendi ancak geri yüklenen Database bütünlük veya veri okuma kontrollerinden geçemedi.") },
  get confirmation_mismatch() { return tx("Yazdığınız Database adı eşleşmiyor.") }, get retention_not_elapsed() { return tx("Rollback kopyasının koruma süresi henüz dolmadı.") },
  get cleanup_not_applicable() { return tx("Bu işlem için temizlik şu anda uygulanamaz.") },
  get NotClaimed() { return tx("Restore süresi içinde Agent tarafından alınmadı. Hedef değiştirilmedi.") },
  get precheck_rejected() { return tx("Ön kontrol bu geri yükleme ile eşleşmiyor veya başarılı değil. Ön kontrolü yeniden çalıştırın.") },
  get agent_transfer_required() { return tx("Replica başka bir Agent üzerinde. Agentlar arası aktarım desteklenmiyor.") },
  get no_accessible_replica() { return tx("Bu Agent tarafından erişilebilen Replica yok.") }, get replica_not_restorable() { return tx("Replica geri yüklemeye uygun değil.") },
  get replica_not_verified() { return tx("Replica doğrulanmadı.") }, get checksum_unavailable() { return tx("Artifact checksum bilgisi yok.") }, get backup_type_unsupported() { return tx("Backup türü desteklenmiyor.") },
  get overwrite_confirmation_required() { return tx("Üzerine yazmayı hedef Database adıyla onaylayın.") },
  },
  storageConnection: {
    get concurrent_update() { return tx('Bağlantı başka bir işlemle aynı anda değiştirildi. Sayfayı yenileyip tekrar deneyin.') },
    get rotation_test_failed() { return tx('Yeni kimlik bilgisi veya sunucu parmak izi doğrulanamadı. Mevcut bağlantı ve kimlik bilgisi korunuyor.') },
  },
}
// Codes whose text must win over the generic 403/404 wording.
const statusSpecificCodes: Record<string, true> = { tenant_not_found: true, user_not_found: true, membership_not_found: true, agent_not_found: true, plan_not_found: true, quota_policy_not_found: true, subscription_not_found: true, platform_admin_required: true, enrollment_disabled: true, storage_connection_not_found: true, platform_managed_provider: true, notification_channel_not_found: true, self_approval_forbidden: true, not_requester: true, decrypt_not_permitted: true, key_not_found: true, artifact_not_found: true }
const lookup = (table: Record<string, string>, code: string) => Object.hasOwn(table, code) ? table[code] : undefined

export function failureMessage(code?: string | null, scope?: ErrorScope): string {
  if (!code) return tx('İşlem başarısız oldu.')
  const known = (scope && lookup(scopedMessages[scope], code)) ?? lookup(messages, code)
  return known ?? (/^[A-Za-z0-9_.-]{1,80}$/.test(code) ? tx("İşlem başarısız oldu ({0}).", { "0": code }) : tx('İşlem başarısız oldu.'))
}

/** Failures that carry a request body the user can fix: the scoped wording applies only there, never to auth, missing-resource or server failures. */
const isRequestFailure = (error: ApiError) => error.status !== 401 && error.status !== 403 && error.status !== 404 && error.status < 500

/** What happened, in plain words. A single sentence, used wherever a full notice does not fit (dialogs, inline hints). */
export function errorMessage(error: unknown, scope?: ErrorScope): string {
  if (error instanceof ApiError) {
    if (error.status === 401) return tx('Oturumunuz sona erdi. Yeniden giriş yapın.')
    const code = error.reason ?? error.code
    if (scope && isRequestFailure(error)) return failureMessage(code, scope)
    if (error.code === 'support_access_denied') return failureMessage(code)
    if (error.code === 'support_session_not_found' || error.code === 'not_session_owner' || error.code === 'replace_forbidden' || error.code === 'support_unavailable') return failureMessage(code)
    if ((error.status === 403 || error.status === 404) && code && Object.hasOwn(statusSpecificCodes, code)) return messages[code]
    if (error.status === 403) return tx('Bu işlem için yetkiniz yok.')
    if (error.status === 404) return tx('Kayıt bulunamadı veya bu tenant altında erişilemiyor.')
    if (error.status >= 500) return tx('Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.')
    return failureMessage(code)
  }
  return tx('Sunucuya ulaşılamadı. Bağlantınızı kontrol edip tekrar deneyin.')
}

export interface ErrorDescription {
  /** What happened. */
  what: string
  /** What it means for the user's data. */
  dataImpact: string
  /** The one thing to do next. */
  action: string
  /** Raw code, status and field: for the collapsed technical details only. */
  technical: TechnicalItem[]
}

const subscriptionUnchanged = () => tx("Abonelik ve depolama limiti değişmedi; yedekleriniz etkilenmedi.")

// Codes whose consequence for the data is specific; every other failure gets the honest generic line for its kind.
const guidance: Record<string, { dataImpact: () => string; action: () => string }> = {
  quota_exceeded: { dataImpact: () => tx("Mevcut yedekleriniz silinmedi ve geri yüklenebilir."), action: () => tx("Depolama alanı açmak için yöneticinize danışın.") },
  quota_size_unknown: { dataImpact: () => tx("Mevcut yedekleriniz silinmedi ve geri yüklenebilir."), action: () => tx("Yöneticinize danışın.") },
  QuotaExceeded: { dataImpact: () => tx("Mevcut yedekleriniz silinmedi ve geri yüklenebilir."), action: () => tx("Depolama alanı açmak için yöneticinize danışın.") },
  integrity_mismatch: { dataImpact: () => tx("Dosya doğrulanamadığı için kullanılmadı; yedeğiniz değişmedi."), action: () => tx("Yeni bir indirme başlatın.") },
  AgentLost: { dataImpact: () => tx("Bu deneme tamamlanmadı; önceki başarılı yedekleriniz korunur."), action: () => tx("Yedekleme bilgisayarının açık ve bağlı olduğunu kontrol edip yeniden deneyin.") },
  connection_in_use: { dataImpact: () => tx("Bağlantı silinmedi; yedeklemeler kullanmaya devam ediyor."), action: () => tx("Önce bu bağlantıyı kullanan işleri başka bir depolamaya taşıyın.") },
  private_key_rejected: { dataImpact: () => tx("Özel anahtar gönderilmedi ve kaydedilmedi."), action: () => tx("Yalnızca genel anahtarı girin.") },
  plan_already_assigned: { dataImpact: () => subscriptionUnchanged(), action: () => tx("Farklı bir plan seçin.") },
  plan_disabled: { dataImpact: () => subscriptionUnchanged(), action: () => tx("Etkin bir plan seçin.") },
  subscription_not_active: { dataImpact: () => subscriptionUnchanged(), action: () => tx("Abonelik durumu yenilendi; geçerli işlemlerden birini seçin.") },
  subscription_not_suspended: { dataImpact: () => subscriptionUnchanged(), action: () => tx("Abonelik durumu yenilendi; geçerli işlemlerden birini seçin.") },
  subscription_not_open: { dataImpact: () => subscriptionUnchanged(), action: () => tx("Yeni bir plan atayın.") },
  subscription_expired: { dataImpact: () => subscriptionUnchanged(), action: () => tx("Yeni bir plan atayın.") },
  subscription_not_found: { dataImpact: () => subscriptionUnchanged(), action: () => tx("Önce bir plan atayın.") },
}

function technicalItems(error: unknown): TechnicalItem[] {
  if (!(error instanceof ApiError)) return []
  const code = error.reason ?? error.code
  const items: TechnicalItem[] = []
  if (code && /^[A-Za-z0-9_.-]{1,80}$/.test(code)) items.push({ label: tx("Hata kodu"), value: code })
  items.push({ label: tx("HTTP durumu"), value: String(error.status) })
  if (error.field && /^[A-Za-z0-9_.-]{1,80}$/.test(error.field)) items.push({ label: tx("Alan"), value: error.field })
  return items
}

/** A failure reported as a bare code (a run, a replica, a restore), not as a failed request. */
export function describeFailure(code: string | null | undefined, scope?: ErrorScope): ErrorDescription {
  const what = failureMessage(code, scope)
  const technical = code && /^[A-Za-z0-9_.-]{1,80}$/.test(code) ? [{ label: tx("Hata kodu"), value: code }] : []
  const key = code ?? ''
  if (Object.hasOwn(guidance, key)) return { what, dataImpact: guidance[key].dataImpact(), action: guidance[key].action(), technical }
  if (scope === 'restore') return { what, dataImpact: tx("Yedeğiniz değişmedi; hedef veritabanının durumunu kontrol edin."), action: tx("Ayrıntıları kontrol edip geri yüklemeyi yeniden deneyin."), technical }
  return { what, dataImpact: tx("Mevcut yedekleriniz etkilenmedi."), action: tx("Ayrıntıları kontrol edip yeniden deneyin."), technical }
}

/** A permission wall is a failure too: nothing was changed, and the way forward is a person who can grant access. */
export function accessDenied(what: string): ErrorDescription {
  return { what, dataImpact: tx("Bu işlem uygulanmadı; verileriniz değişmedi."), action: tx("Yöneticinize danışın."), technical: [] }
}

/** A record that is gone or not theirs: nothing changed, and the way forward is to pick again from the list. */
export function notFound(what: string): ErrorDescription {
  return { what, dataImpact: tx("Bu işlem uygulanmadı; verileriniz değişmedi."), action: tx("Sayfayı yenileyip kaydı listeden yeniden seçin."), technical: [] }
}

/** Every failure the user sees says what happened, what it means for the data and what to do next. */
export function describeError(error: unknown, scope?: ErrorScope): ErrorDescription {
  const what = errorMessage(error, scope)
  const technical = technicalItems(error)
  const key = error instanceof ApiError ? error.reason ?? error.code ?? '' : ''
  const known = Object.hasOwn(guidance, key) ? guidance[key] : undefined
  if (known) return { what, dataImpact: known.dataImpact(), action: known.action(), technical }
  const uncertain = tx("Mevcut yedekleriniz etkilenmedi; işlemin tamamlanıp tamamlanmadığını listeden kontrol edin.")
  if (!(error instanceof ApiError)) return { what, dataImpact: uncertain, action: tx("İnternet bağlantınız düzeldiğinde işlemi tekrarlayın."), technical }
  if (error.status === 401) return { what, dataImpact: tx("Bu işlem uygulanmadı; verileriniz değişmedi."), action: tx("Giriş yaptıktan sonra işlemi tekrarlayın."), technical }
  if (error.status === 403) return { what, dataImpact: tx("Bu işlem uygulanmadı; verileriniz değişmedi."), action: tx("Yöneticinize danışın."), technical }
  if (error.status === 404) return { what, dataImpact: tx("Bu işlem uygulanmadı; verileriniz değişmedi."), action: tx("Sayfayı yenileyip kaydı listeden yeniden seçin."), technical }
  if (error.status >= 500) return { what, dataImpact: uncertain, action: tx("Sorun sürerse teknik ayrıntıları destek ekibine iletin."), technical }
  return { what, dataImpact: tx("Bu işlem uygulanmadı; verileriniz değişmedi."), action: tx("Bilgileri kontrol edip tekrar deneyin."), technical }
}
