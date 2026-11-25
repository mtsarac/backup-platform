import { describe, expect, it } from 'vitest'
import { ApiError } from './client'
import { changeLanguage } from '@/lib/i18n/runtime'
import { actionLabel, describeError, describeFailure, errorMessage, failureMessage } from './failure-messages'

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
    [409, { error: 'admission_denied', code: 'quota_bounded_upload_unsupported' }, 'Yedekleme bilgisayarı, yükleme öncesinde kesin depolama kotasını uygulamayı desteklemiyor; yedekleme bilgisayarını güncelleyin. Yeni yedekleme başlatılmadı.'],
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
    expect(errorMessage(new ApiError(404))).toBe("Kayıt bulunamadı veya bu çalışma alanı kapsamında erişilemiyor.")
  })
})

describe('error description', () => {
  it('is a plain message for every kind of failure, with no generic data reassurance', () => {
    const failures: unknown[] = [new ApiError(401), new ApiError(403), new ApiError(404), new ApiError(409, { error: 'already_member' }), new ApiError(500), new Error('offline')]
    for (const failure of failures) {
      const { what, dataImpact } = describeError(failure)
      expect(what.trim().length).toBeGreaterThan(0)
      expect(dataImpact).toBeUndefined()
    }
  })

  it('keeps the plain message as the first part', () => {
    expect(describeError(new ApiError(500)).what).toBe(errorMessage(new ApiError(500)))
  })

  it('adds a next step only where one is known', () => {
    expect(describeError(new ApiError(500)).action).toBeUndefined()
    expect(describeError(new ApiError(409, { error: 'admission_denied', code: 'quota_exceeded' })).action).toBe('Depolama alanında yer açın.')
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

  it('tells a rejected storage login from a changed SFTP host key and does not call either a database or generic permission problem', () => {
    const login = failureMessage('StorageAuthenticationFailed')
    const hostKey = failureMessage('StorageHostKeyMismatch')
    expect(login).toMatch(/reddedildi/)
    expect(hostKey).toMatch(/parmak iziyle uyuşmuyor/)
    expect(login).not.toBe(hostKey)
    expect([login, hostKey]).not.toContain(failureMessage('PermissionDenied'))
    expect([login, hostKey]).not.toContain(failureMessage('AuthenticationFailed'))
  })

  it('names an untrusted server certificate instead of showing the raw code, for backups and restores alike', () => {
    for (const scope of [undefined, 'restore' as const]) {
      const message = failureMessage('ServerCertificateUntrusted', scope)
      expect(message).toMatch(/sertifikası doğrulanamadı/)
      expect(message).not.toContain('ServerCertificateUntrusted')
      expect(message).not.toBe(failureMessage('DatabaseUnavailable'))
    }
  })

  it('looks up a bare failure code in the scope too', () => {
    expect(failureMessage('NotClaimed', 'restore')).toContain('Hedef değiştirilmedi')
  })
})

const unknownSentence = 'Beklenmeyen bir sorun oluştu. Biraz sonra yeniden deneyin; sürerse aşağıdaki kodu destek ekibine iletin.'

describe('failure classes', () => {
  it.each([
    ['DatabaseUnavailable', undefined, 'Bilgisayarı kontrol et'],
    ['agent_unavailable', undefined, 'Bilgisayarı kontrol et'],
    ['OperationTimedOut', undefined, 'Bilgisayarı kontrol et'],
    ['AuthenticationFailed', undefined, 'Bilgileri düzelt'],
    ['StorageAuthenticationFailed', undefined, 'Bilgileri düzelt'],
    ['authentication_failed', undefined, 'Bilgileri düzelt'],
    ['StorageHostKeyMismatch', undefined, 'Bilgileri düzelt'],
    ['QuotaExceeded', undefined, 'Depolamayı yönet'],
    ['quota_exceeded', undefined, 'Depolamayı yönet'],
    ['ScheduleGraceExpired', undefined, 'Şimdi yedekle'],
    ['SqlRestoreFailed', 'restore', 'Yeniden dene'],
    ['TargetInUse', 'restore', 'Yeniden dene'],
  ] as const)('offers the one action of its class for %s', (code, scope, label) => {
    const { actionKind } = describeFailure(code, scope)
    expect(actionKind && actionLabel(actionKind)).toBe(label)
  })

  it('offers no class action for an untrusted server certificate; trusting it is a separate, explicit step', () => {
    expect(describeFailure('ServerCertificateUntrusted').actionKind).toBeUndefined()
  })

  it('keeps the raw code out of the message and in the technical details', () => {
    const { what, action, technical } = describeFailure('StorageAuthenticationFailed')
    for (const part of [what, action]) expect(part ?? '').not.toContain('StorageAuthenticationFailed')
    expect(technical.map(item => item.value)).toEqual(['StorageAuthenticationFailed'])
  })
})

describe('unknown failure code', () => {
  it('falls back to one fixed sentence with the code kept for support, never in the sentence', () => {
    expect(failureMessage('some_new_backend_code')).toBe(unknownSentence)
    const description = describeFailure('some_new_backend_code')
    expect(description.what).toBe(unknownSentence)
    expect(description.technical.map(item => item.value)).toEqual(['some_new_backend_code'])
    expect(errorMessage(new ApiError(409, { error: 'some_new_backend_code' }))).toBe(unknownSentence)
    for (const status of [401, 403, 404, 500]) {
      const error = new ApiError(status, { error: 'some_new_backend_code' })
      expect(errorMessage(error)).toBe(unknownSentence)
      expect(describeError(error).technical.some(item => item.value === 'some_new_backend_code')).toBe(true)
    }
  })

  it('never shows an unsafe code at all', () => {
    expect(describeFailure('<b>x</b>').technical).toEqual([])
  })
})

describe('backend codes that had no mapping', () => {
  const codes = [
    'authentication_failed', 'invalid_configuration', 'timeout', 'network_denied', 'network_unavailable', 'secret_unavailable', 'host_key_mismatch', 'insufficient_space',
    'delivery_not_failed', 'feature_disabled',
    'dns_failed', 'connect_failed', 'no_sender', 'send_failed', 'authentication_none', 'port_not_allowed', 'insecure_authentication', 'incomplete_configuration', 'invalid_recipient', 'public_url_not_configured', 'transport_unavailable', 'insecure_endpoint', 'redirect_not_followed', 'invalid_endpoint', 'secret_missing', 'smtp_auth_failed', 'smtp_rejected', 'smtp_temporary', 'smtp_tls_failed', 'smtp_tls_unavailable', 'smtp_unavailable', 'http_400', 'http_429', 'http_500',
    'credential_unavailable', 'provider_unavailable', 'scan_failed', 'scan_timeout', 'too_many_databases',
    'docker_not_found', 'docker_access_denied', 'docker_timeout', 'docker_failed', 'disabled_by_settings', 'systemd_not_found', 'systemd_not_running', 'systemd_access_denied', 'systemd_failed', 'registry_access_denied', 'registry_failed', 'detector_failed', 'probe_failed', 'no_published_port', 'no_endpoint', 'permission_denied', 'unreachable', 'connection_failed', 'server_certificate_untrusted', 'not_supported',
    'download_unavailable', 'download_already_started', 'download_lease_rejected', 'download_invalid_configuration', 'download_source_missing', 'download_source_inaccessible',
    'download_integrity_mismatch', 'download_agent_unavailable', 'download_cancelled', 'download_internal_error', 'cancelled_by_revocation',
    'storage_target_unknown', 'invalid_location', 'delete_not_supported', 'delete_not_verified',
    'LeaseLost', 'DestinationNotConfigured', 'repair_not_found',
    'CapacityUnavailable', 'TargetNotFound', 'ConnectionFailed',
    'protocol_incompatible', 'protocol_missing', 'rate_limited', 'lease_conflict', 'destination_not_authorized', 'destination_unavailable', 'source_unavailable', 'source_inaccessible', 'unsupported_provider', 'agent_direct_required', 'attempt_not_terminal',
    'share_not_found', 'share_expired', 'share_revoked', 'already_revoked', 'cannot_shorten', 'duration_exceeded', 'invalid_duration', 'missing_expiry', 'no_downloadable_replica', 'invalid_artifact',
    'invalid_cursor', 'invalid_limit', 'invalid_status', 'invalid_result', 'invalid_tenant', 'invalid_date_range', 'invalid_report_period',
    'too_many_attempts', 'invalid_password', 'password_same_as_current', 'invalid_token', 'email_not_verifiable', 'email_recovery_unavailable', 'last_platform_admin', 'invalid_recovery_key',
    'google_drive_authorization_unavailable', 'forbidden', 'unauthorized', 'retry_not_allowed', 'invalid_run_state', 'invalid_schedule', 'agent_cannot_restore', 'invalid_lifetime', 'invalid_server_url',
  ]

  it.each(codes)('maps %s to a Turkish and an English sentence, not to the unexpected-problem fallback', (code) => {
    const turkish = failureMessage(code)
    changeLanguage('en')
    const english = failureMessage(code)
    changeLanguage('tr')
    for (const text of [turkish, english]) {
      expect(text).not.toMatch(/Beklenmeyen bir sorun|unexpected problem/i)
      expect(text).not.toContain(code)
    }
    expect(english).not.toBe(turkish)
  })

  it('words a repeated delete failure code in the delete scope, not as a plan limit', () => {
    expect(failureMessage('unexpected', 'delete')).not.toBe(failureMessage('unexpected'))
  })
})
