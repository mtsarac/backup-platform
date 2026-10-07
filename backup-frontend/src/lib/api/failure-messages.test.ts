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
  ])('maps %s %j', (status, body, text) => {
    expect(errorMessage(new ApiError(status, body))).toBe(text)
  })

  it('keeps generic wording for codeless 403 and 404', () => {
    expect(errorMessage(new ApiError(403))).toBe('Bu işlem için yetkiniz yok.')
    expect(errorMessage(new ApiError(404, { error: 'something_else' }))).toBe('Kayıt bulunamadı veya bu tenant altında erişilemiyor.')
  })
})
