import { describe, expect, it } from 'vitest'
import { ApiError } from './client'
import { errorMessage } from './failure-messages'

describe('platform failure messages', () => {
  it.each([
    [409, { error: 'already_member' }, 'Bu kullanıcı zaten tenant üyesi.'],
    [404, { error: 'user_not_found' }, 'Kullanıcı bulunamadı.'],
    [404, { error: 'tenant_not_found' }, 'Tenant bulunamadı.'],
    [404, { error: 'membership_not_found' }, 'Tenant üyeliği bulunamadı. Liste güncel olmayabilir.'],
    [409, { error: 'login_taken' }, 'Bu kullanıcı adı zaten kullanılıyor.'],
    [403, { error: 'platform_admin_required' }, 'Platform Administration için PlatformAdmin yetkisi gerekir.'],
    [400, { error: 'invalid_request', field: 'password', code: 'invalid_length' }, 'Girilen değerin uzunluğu geçersiz.'],
    [400, { error: 'invalid_request', field: 'name' }, 'Girilen değer geçersiz. Yapılandırmayı kontrol edin.'],
    [404, { error: 'plan_not_found' }, 'Plan bulunamadı. Liste güncel olmayabilir.'],
    [404, { error: 'subscription_not_found' }, 'Tenant için abonelik bulunamadı.'],
    [409, { error: 'plan_name_taken' }, 'Bu plan adı zaten kullanılıyor.'],
    [409, { error: 'system_plan_immutable' }, 'Sistem planı değiştirilemez.'],
    [409, { error: 'concurrent_update' }, 'Plan başka bir işlemle aynı anda değiştirildi. Sayfayı yenileyip tekrar deneyin.'],
    [409, { error: 'plan_disabled' }, 'Bu plan devre dışı; yeni atamalarda seçilemez.'],
    [409, { error: 'plan_already_assigned' }, 'Tenant zaten bu planı kullanıyor.'],
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
    [409, { error: 'admission_denied', code: 'quota_exceeded' }, 'Depolama kotası doldu; yeni backup başlatılamadı. Mevcut backup\'lar silinmedi ve geri yüklenebilir.'],
    [409, { error: 'admission_denied', code: 'subscription_required' }, 'Etkin bir abonelik bulunmadığı için yeni backup başlatılamadı.'],
    [409, { error: 'quota_policy_key_taken' }, 'Bu kota politikası anahtarı zaten kullanılıyor.'],
  ])('maps %s %j', (status, body, text) => {
    expect(errorMessage(new ApiError(status, body))).toBe(text)
  })

  it('keeps generic wording for codeless 403 and 404', () => {
    expect(errorMessage(new ApiError(403))).toBe('Bu işlem için yetkiniz yok.')
    expect(errorMessage(new ApiError(404, { error: 'something_else' }))).toBe('Kayıt bulunamadı veya bu tenant altında erişilemiyor.')
  })
})
