import { describe, expect, it } from 'vitest'
import { ApiError } from './client'
import { describeError, errorMessage, failureMessage } from './failure-messages'

describe('platform failure messages', () => {
  it.each([
    [409, { error: 'already_member' }, 'Bu kullanıcı zaten çalışma alanı üyesi.'],
    [404, { error: 'user_not_found' }, 'Kullanıcı bulunamadı.'],
    [404, { error: 'tenant_not_found' }, 'Çalışma alanı bulunamadı.'],
    [404, { error: 'membership_not_found' }, 'Çalışma alanı üyeliği bulunamadı. Liste güncel olmayabilir.'],
    [409, { error: 'login_taken' }, 'Bu kullanıcı adı zaten kullanılıyor.'],
    [403, { error: 'platform_admin_required' }, 'Platform yönetimi için Platform Yöneticisi yetkisi gerekir.'],
    [400, { error: 'invalid_request', field: 'password', code: 'invalid_length' }, 'Girilen değerin uzunluğu geçersiz.'],
    [400, { error: 'invalid_request', field: 'name' }, 'Girilen değer geçersiz. Yapılandırmayı kontrol edin.'],
    [404, { error: 'plan_not_found' }, 'Plan bulunamadı. Liste güncel olmayabilir.'],
    [404, { error: 'subscription_not_found' }, 'Çalışma alanı için abonelik bulunamadı.'],
    [409, { error: 'plan_name_taken' }, 'Bu plan adı zaten kullanılıyor.'],
    [409, { error: 'system_plan_immutable' }, 'Sistem planı değiştirilemez.'],
    [409, { error: 'concurrent_update' }, 'Plan başka bir işlemle aynı anda değiştirildi. Sayfayı yenileyip tekrar deneyin.'],
    [409, { error: 'plan_disabled' }, 'Bu plan devre dışı; yeni atamalarda seçilemez.'],
    [409, { error: 'plan_already_assigned' }, 'Çalışma alanı zaten bu planı kullanıyor.'],
    [409, { error: 'subscription_not_active' }, 'Abonelik etkin değil; askıya alınamaz.'],
    [409, { error: 'subscription_not_suspended' }, 'Abonelik askıda değil; yeniden etkinleştirilemez.'],
    [409, { error: 'subscription_not_open' }, 'Abonelik sona ermiş; yalnızca yeni bir plan atanabilir.'],
    [409, { error: 'subscription_expired' }, 'Abonelik süresi dolmuş; yalnızca yeni bir plan atanabilir.'],
    [400, { error: 'invalid_request', field: 'storageLimit', code: 'required' }, 'Zorunlu bir alan boş bırakıldı.'],
    [400, { error: 'invalid_request', field: 'storageLimit', code: 'ambiguous' }, 'Sınırlı boyut ile limitsiz seçeneği birlikte kullanılamaz.'],
    [400, { error: 'invalid_request', field: 'storageLimit', code: 'out_of_range' }, 'Girilen değer izin verilen aralığın dışında.'],
    [400, { error: 'invalid_request', field: 'storageLimitOverride', code: 'invalid_kind' }, 'Limit türü geçersiz.'],
    [400, { error: 'invalid_request', field: 'storageLimitOverride', code: 'unexpected' }, 'Bu limit türü için bayt değeri girilemez.'],
    [400, { error: 'invalid_request', field: 'name', code: 'invalid_characters' }, 'Girilen değer geçersiz karakterler içeriyor.'],
    [400, { error: 'invalid_request', field: 'endsAt', code: 'invalid_value' }, 'Girilen değer geçersiz.'],
    [409, { error: 'admission_denied', code: 'quota_exceeded' }, 'Depolama kotası doldu; yeni yedekleme başlatılamadı. Mevcut yedekler silinmedi ve geri yüklenebilir.'],
    [409, { error: 'admission_denied', code: 'quota_size_unknown' }, 'Yedek boyutu henüz bilinmediği için kesin depolama kotasında güvenli alan ayrılamadı; yeni yedekleme başlatılmadı. Boyut tahmininin belirlenmesi için yöneticiye başvurun.'],
    [422, { error: 'storage_error', code: 'QuotaExceeded' }, 'Gerçek yedek boyutu kesin depolama kotasında kullanılabilir alanı aşıyor; hedefe yükleme reddedildi.'],
    [409, { error: 'admission_denied', code: 'quota_bounded_upload_unsupported' }, 'Ajan, yükleme öncesinde kesin depolama kotasını uygulamayı desteklemiyor; ajanı güncelleyin. Yeni yedekleme başlatılmadı.'],
    [409, { error: 'admission_denied', code: 'subscription_required' }, 'Etkin bir abonelik bulunmadığı için yeni yedekleme başlatılamadı.'],
    [409, { error: 'admission_denied', code: 'storage_connection_disabled' }, 'Hedef depolama bağlantısı devre dışı; yeni yedekleme başlatılamaz.'],
    [409, { error: 'admission_denied', code: 'storage_connection_unavailable' }, 'Hedef depolama bağlantısı bulunamadı veya bu işin depolama sağlayıcısına ait değil; yeni yedekleme başlatılamaz.'],
    [404, { error: 'storage_connection_not_found' }, 'Depolama bağlantısı bulunamadı. Liste güncel olmayabilir.'],
    [409, { error: 'storage_connection_name_taken' }, 'Bu ada sahip bir depolama bağlantısı zaten var.'],
    [409, { error: 'connection_limit_reached' }, 'Bu depolama sağlayıcısı için bağlantı sınırına ulaşıldı.'],
    [409, { error: 'platform_managed' }, 'Platform yönetimli bağlantıyı yalnızca platform yöneticisi değiştirebilir.'],
    [409, { error: 'tenant_managed' }, 'Çalışma alanı tarafından yönetilen bağlantı için bu işlemi yalnızca çalışma alanı yöneticisi yapabilir.'],
    [409, { error: 'connection_in_use' }, 'Bağlantı hâlâ bir kopya veya yedekleme işi tarafından kullanılıyor; silinemez.'],
    [403, { error: 'platform_managed_provider' }, 'Bu depolama sağlayıcısı yalnızca platform tarafından yönetilir; çalışma alanı bağlantı ekleyemez.'],
    [400, { error: 'invalid_request', field: 'providerType', code: 'provider_not_available' }, 'Bu depolama sağlayıcısı henüz kullanılamıyor.'],
    [400, { error: 'invalid_request', field: 'secret', code: 'secrets_not_supported' }, 'Bu depolama sağlayıcısı kimlik bilgisi kabul etmiyor.'],
    [400, { error: 'invalid_request', field: 'configuration.foo', code: 'unknown_field' }, 'Bu depolama sağlayıcısı için tanımlı olmayan bir alan gönderildi.'],
    [409, { error: 'quota_policy_key_taken' }, 'Bu kota politikası anahtarı zaten kullanılıyor.'],
  ])('maps %s %j', (status, body, text) => {
    expect(errorMessage(new ApiError(status, body))).toBe(text)
  })

  it('keeps generic wording for codeless 403 and 404', () => {
    expect(errorMessage(new ApiError(403))).toBe('Bu işlem için yetkiniz yok.')
    expect(errorMessage(new ApiError(404, { error: 'something_else' }))).toBe("Kayıt bulunamadı veya bu çalışma alanı kapsamında erişilemiyor.")
  })
})

describe('three-part error description', () => {
  it('says what happened, what it means for the data and what to do, for every kind of failure', () => {
    const failures: unknown[] = [new ApiError(401), new ApiError(403), new ApiError(404), new ApiError(409, { error: 'already_member' }), new ApiError(500), new Error('offline')]
    for (const failure of failures) {
      const { what, dataImpact, action } = describeError(failure)
      expect([what, dataImpact, action].every(part => part.trim().length > 0)).toBe(true)
      expect(new Set([what, dataImpact, action]).size).toBe(3)
    }
  })

  it('keeps the plain message as the first part', () => {
    expect(describeError(new ApiError(500)).what).toBe(errorMessage(new ApiError(500)))
  })

  it('reassures about stored backups for quota failures', () => {
    const { dataImpact } = describeError(new ApiError(409, { error: 'admission_denied', code: 'quota_exceeded' }))
    expect(dataImpact).toContain('silinmedi')
  })

  it('moves the raw code, status and field into technical details, never the plain message', () => {
    const { technical } = describeError(new ApiError(400, { error: 'invalid_request', field: 'name', code: 'invalid_length' }))
    expect(technical.map(item => item.value)).toEqual(['invalid_length', '400', 'name'])
    expect(describeError(new Error('offline')).technical).toEqual([])
  })

  it('never exposes a code that is not a plain identifier', () => {
    expect(describeError(new ApiError(400, { code: '<script>alert(1)</script>' })).technical.map(item => item.value)).toEqual(['400'])
  })

  it('uses the restore wording for restore failures and the connection wording for connection conflicts', () => {
    expect(errorMessage(new ApiError(409, { error: 'restore_rejected', reason: 'TargetDatabaseExists' }), 'restore')).toBe('Hedef veritabanı zaten var. Yeni veritabanı modu mevcut veritabanının üzerine yazmaz.')
    expect(errorMessage(new ApiError(409, { error: 'concurrent_update' }), 'storageConnection')).toContain('Bağlantı')
    expect(errorMessage(new ApiError(500), 'restore')).toBe(errorMessage(new ApiError(500)))
  })

  it('looks up a bare failure code in the scope too', () => {
    expect(failureMessage('NotClaimed', 'restore')).toContain('Hedef değiştirilmedi')
  })
})
