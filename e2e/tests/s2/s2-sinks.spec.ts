// Real backend round-trip & sink verification on the isolated bps2 stack (bash e2e/s2-stack.sh):
// - S3 BackendReadAccess against bps2-minio (wrong secret -> "Bilgileri düzelt" -> "Bağlantı çalışıyor" + re-test + edit + secret rotation)
// - SFTP BackendRoundTrip against bps2-sftp ("Bağlantı çalışıyor")
// - Central BackendRoundTrip probe on /platform/storage/connections ("Çalışıyor")
// - SMB behind Features__smb=true
// - SMTP -> bps2-mailpit (recipient confirmation link, verified test email, unsubscribe link)
// - HTTPS Webhook -> bps2-webhook (HMAC X-Backup-Signature, permanent 404 failure -> platform retry -> Sent)
// - SMS behind Features__sms=true -> bps2-webhook/sms (validation, Bearer token secret set/clear, live delivery)
import { createHmac } from 'node:crypto'
import { expect, test } from '@playwright/test'
import { Api } from '../../api'
import {
  clearWebhookSink,
  hasS2Stack,
  mailpitMessageBody,
  mailpitMessages,
  s2State,
  seedIsolatedTenant,
  setWebhookFailMode,
  sftpFingerprint,
  signInAs,
  uniq,
  watch,
  webhookRequests,
  type IsolatedTenant,
} from './helpers'

test.skip(!hasS2Stack(), 'Requires isolated bps2 stack; run `bash e2e/s2-stack.sh`.')

const s2 = hasS2Stack() ? s2State() : null!
let t: IsolatedTenant

test.beforeAll(async () => {
  t = await seedIsolatedTenant('sinks', {
    baseApiUrl: s2.apiUrl,
    platformAccount: s2.accounts.platformAdmin,
  })
  const pApi = await Api.signIn(s2.accounts.platformAdmin, s2.apiUrl)
  await pApi.call(
    'POST',
    `/api/platform/tenants/${s2.tenantId}/storage-connections`,
    { providerType: 'central', name: 'Managed Storage', ownership: 'PlatformManaged' },
    [200, 201, 409],
  )
  await pApi.dispose()
})

test.afterAll(async () => {
  await t?.api.dispose()
})

const signTenant = (page: any) => signInAs(page, t.account, { baseUrl: s2.baseUrl, apiUrl: s2.apiUrl })
const signPlatform = (page: any) => signInAs(page, s2.accounts.platformAdmin, { baseUrl: s2.baseUrl, apiUrl: s2.apiUrl })
const url = (path: string) => `${s2.baseUrl}${path}`

test.describe('bps2 real storage round-trips (S3, SFTP, Central, SMB)', () => {
  test('S3 BackendReadAccess against MinIO: bad credentials fail test, "Bilgileri düzelt" fixes in place, re-tests, edits, rotates secret and deletes', async ({ page }) => {
    test.setTimeout(90_000)
    await signTenant(page)
    const problems = watch(page)
    const name = uniq('s3-real')
    const renamed = `${name}-r`

    await page.goto(url(`/tenants/${t.tenantId}/storage/connections/new`))
    await page.getByRole('radio', { name: /^S3/ }).check()
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByLabel('HTTPS uç noktası').fill('https://bps2-minio:9000')
    await page.getByLabel('Kova').fill('s2bucket')
    await page.getByLabel('İmzalama bölgesi').fill('us-east-1')
    await page.getByLabel('Erişim anahtarı').fill('s2minio')
    await page.getByLabel('Gizli anahtar').fill('wrong-secret-key')
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByRole('button', { name: 'Adı değiştir' }).click()
    await page.getByRole('textbox', { name: 'Ad', exact: true }).fill(name)
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()

    await expect(page.getByText('Test başarısız')).toBeVisible()
    await page.getByRole('button', { name: 'Bilgileri düzelt' }).click()
    await page.getByLabel('Erişim anahtarı').fill('s2minio')
    await page.getByLabel('Gizli anahtar').fill('S2-minio-pass-1')
    await page.getByRole('button', { name: 'Kaydet ve yeniden test et' }).click()

    await expect(page.getByText('Bağlantı çalışıyor')).toBeVisible()
    await expect(page.getByText(/Bucket erişimi|Kova erişimi/i)).toBeVisible()
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()
    await expect(page.getByRole('article', { name })).toContainText('Sağlıklı')

    // Re-test from detail, rename, rotate secret against live MinIO and verify test stays green
    await page.getByRole('link', { name }).click()
    await page.getByRole('button', { name: `Test et — ${name}` }).click()
    await expect(page.getByText('Sağlıklı').first()).toBeVisible()

    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    await page.getByRole('dialog').getByLabel('Ad', { exact: true }).fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('heading', { name: renamed })).toBeVisible()

    await page.getByRole('tab', { name: 'Kimlik bilgisi' }).click()
    await page.getByRole('button', { name: `Kimlik bilgisini yenile — ${renamed}` }).click()
    await page.getByRole('dialog').getByLabel('Erişim anahtarı').fill('s2minio')
    await page.getByRole('dialog').getByLabel('Gizli anahtar').fill('S2-minio-pass-1')
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()
    await page.getByRole('button', { name: `Test et — ${renamed}` }).click()
    await expect(page.getByText('Sağlıklı').first()).toBeVisible()

    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    expect(problems).toEqual([])
  })

  test('SFTP BackendRoundTrip: writes, verifies and deletes a real probe object on bps2-sftp', async ({ page }) => {
    test.setTimeout(60_000)
    await signTenant(page)
    const problems = watch(page)
    const name = uniq('sftp-real')

    await page.goto(url(`/tenants/${t.tenantId}/storage/connections/new`))
    await page.getByRole('radio', { name: /^SFTP/ }).check()
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByLabel('Sunucu adresi').fill('bps2-sftp')
    await page.getByLabel('Bağlantı noktası').fill('22')
    await page.getByLabel('Kullanıcı adı').fill('s2user')
    await page.getByLabel('Yedeklerin saklanacağı klasör').fill('/upload')
    await page.getByLabel('Sunucunun güvenlik parmak izi (SHA256)').fill(sftpFingerprint())
    await page.getByLabel('Parola', { exact: true }).fill('S2-sftp-pass1')
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByRole('button', { name: 'Adı değiştir' }).click()
    await page.getByRole('textbox', { name: 'Ad', exact: true }).fill(name)
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()

    await expect(page.getByText('Bağlantı çalışıyor')).toBeVisible({ timeout: 15_000 })
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()
    await page.getByRole('link', { name }).click()
    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: `Sil — ${name}` }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(name)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    expect(problems).toEqual([])
  })

  test('Central BackendRoundTrip: platform-managed Central connection passes probe write/read/delete test', async ({ page }) => {
    await signPlatform(page)
    const problems = watch(page)
    await page.goto(url('/platform/storage/connections'))
    await page.getByLabel(/Depolama hedefi ara|Ara/i).fill('E2E Tenant')
    const row = page.getByRole('row').filter({ hasText: 'E2E Tenant' }).first()
    await expect(row).toBeVisible()
    await row.getByRole('button', { name: /^Test et — /i }).click()
    await expect(row).toContainText('Yazma, doğrulama ve silme sınaması başarılı.')
    expect(problems).toEqual([])
  })

  test('SMB provider (behind smb feature flag): wizard validation, create, edit, rotate secret, disable/enable, delete', async ({ page }) => {
    test.setTimeout(60_000)
    await signTenant(page)
    const problems = watch(page)
    const name = uniq('smb')
    const renamed = `${name}-r`

    await page.goto(url(`/tenants/${t.tenantId}/storage/connections/new`))
    await page.getByRole('radio', { name: /^SMB \/ NAS/ }).check()
    await page.getByRole('button', { name: 'İleri' }).click()
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()

    await page.getByLabel('NAS sunucusu').fill('192.168.122.1')
    await page.getByLabel('Paylaşım adı').fill('Data')
    await page.getByLabel('Kullanıcı adı').fill('bpe2e')
    await page.getByLabel('Parola').fill('S2-smb-pass1')
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByRole('button', { name: 'Adı değiştir' }).click()
    await page.getByRole('textbox', { name: 'Ad', exact: true }).fill(name)
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByText('Bağlantı kaydedildi')).toBeVisible()
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()

    await page.getByRole('link', { name }).click()
    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    await page.getByRole('dialog').getByLabel('Ad', { exact: true }).fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('heading', { name: renamed })).toBeVisible()

    await page.getByRole('tab', { name: 'Kimlik bilgisi' }).click()
    await page.getByRole('button', { name: `Kimlik bilgisini yenile — ${renamed}` }).click()
    await page.getByRole('dialog').getByLabel('Kullanıcı adı').fill('bpe2e')
    await page.getByRole('dialog').getByLabel('Parola').fill('S2-smb-pass2')
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()

    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Devre dışı bırak' }).click()
    await expect(page.getByText(/Bu hedef.*devre dışı/)).toBeVisible()
    await page.getByRole('button', { name: `Etkinleştir — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Etkinleştir' }).click()
    await expect(page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` })).toBeVisible()

    await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    expect(problems).toEqual([])
  })
})

test.describe('bps2 real notification sinks (Mailpit SMTP, HTTPS Webhook + retry, SMS)', () => {
  test('Email SMTP -> Mailpit: platform enables SMTP, tenant confirms recipient via email link, test alert arrives, unsubscribe works', async ({ page }) => {
    test.setTimeout(90_000)
    await signPlatform(page)
    const problems = watch(page)
    await page.goto(url('/platform/notification-transports'))
    await page.getByLabel('E-posta aktarımı etkin').check()
    await page.getByLabel('Gönderen adresi').fill('alerts@s2.e2e.test')
    await page.getByLabel('Gönderen adı').fill('S2 Backup Platform')
    await page.getByRole('button', { name: 'Teknik ayrıntılar' }).first().click()
    await page.getByLabel('Sunucu').fill('bps2-mailpit')
    await page.getByLabel('Bağlantı noktası').fill('1025')
    await page.getByLabel('Güvenlik', { exact: true }).selectOption('none')
    await page.getByLabel('Kullanıcı adı').fill('')
    await page.getByRole('button', { name: 'E-posta ayarlarını kaydet' }).click()
    await expect(page.getByText('E-posta aktarımı kaydedildi.')).toBeVisible()
    await expect(page.getByText('Çalışıyor').first()).toBeVisible()

    // Tenant creates email channel -> confirmation mail lands in Mailpit
    await signTenant(page)
    const addr = `${uniq('ops')}@e2e.test`
    const chanName = uniq('mail-live')
    await page.goto(url(`/tenants/${t.tenantId}/settings?tab=channels`))
    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Ad', { exact: true }).fill(chanName)
    await dialog.getByLabel('E-posta adresi').fill(addr)
    await dialog.getByRole('button', { name: 'Kanal ekle' }).click()
    await expect(dialog).toBeHidden()

    let confirmMsgId = ''
    await expect
      .poll(async () => {
        const msgs = await mailpitMessages()
        const found = msgs.find((m) => m.To.some((r) => r.Address === addr))
        confirmMsgId = found?.ID ?? ''
        return Boolean(confirmMsgId)
      }, { timeout: 15_000 })
      .toBeTruthy()

    const confirmBody = await mailpitMessageBody(confirmMsgId)
    const confirmMatch = confirmBody.Text.match(/https:\/\/localhost:7473\/api\/notifications\/recipient\/confirm#token=[^\s]+/)
    expect(confirmMatch, 'confirmation URL in email').toBeTruthy()

    // Click Confirm in real browser
    await page.goto(confirmMatch![0])
    await page.getByRole('button', { name: 'Confirm' }).click()
    await expect(page.getByText('Thank you. This address will receive Backup Platform alert emails.')).toBeVisible()

    // Back in tenant settings: channel is now verified ("Adres onaylandı"); send real test email
    await page.goto(url(`/tenants/${t.tenantId}/settings?tab=channels`))
    const card = page.getByRole('article', { name: chanName })
    await expect(card).toContainText('Adres onaylandı')
    await page.getByRole('button', { name: `Test gönder — ${chanName}` }).click()

    let testMsgId = ''
    await expect
      .poll(async () => {
        const msgs = await mailpitMessages()
        const found = msgs.find((m) => m.ID !== confirmMsgId && m.To.some((r) => r.Address === addr))
        testMsgId = found?.ID ?? ''
        return Boolean(testMsgId)
      }, { timeout: 15_000 })
      .toBeTruthy()

    await page.getByRole('tab', { name: 'Gönderim geçmişi' }).click()
    await expect(page.getByRole('listitem').filter({ hasText: chanName }).first()).toContainText('Gönderildi')

    // Unsubscribe link from the alert email
    const testBody = await mailpitMessageBody(testMsgId)
    const unsubMatch = testBody.Text.match(/https:\/\/localhost:7473\/api\/notifications\/recipient\/unsubscribe#token=[^\s]+/)
    expect(unsubMatch, 'unsubscribe URL in alert email').toBeTruthy()
    await page.goto(unsubMatch![0])
    await page.getByRole('button', { name: 'Unsubscribe' }).click()
    await expect(page.getByText(/You are unsubscribed/)).toBeVisible()
    expect(problems).toEqual([])
  })

  test('HTTPS Webhook -> bps2-webhook: verifies HMAC signature, permanent 404 failure, and platform failed-delivery retry', async ({ page }) => {
    test.setTimeout(90_000)
    await clearWebhookSink()
    await setWebhookFailMode(true)
    await signTenant(page)
    const problems = watch(page)

    const okName = uniq('hook-ok')
    const secret = 's2-hmac-signing-secret'
    await page.goto(url(`/tenants/${t.tenantId}/settings?tab=channels`))
    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    let dialog = page.getByRole('dialog')
    await dialog.getByLabel('Tür').selectOption('webhook')
    await dialog.getByLabel('Ad', { exact: true }).fill(okName)
    await dialog.getByLabel('Web kancası adresi').fill('https://bps2-webhook/hook')
    await dialog.getByLabel('Gizli anahtar').fill(secret)
    await dialog.getByRole('button', { name: 'Kanal ekle' }).click()
    await expect(dialog).toBeHidden()

    await page.getByRole('button', { name: `Test gönder — ${okName}` }).click()
    let hit: Awaited<ReturnType<typeof webhookRequests>>[number] | undefined
    await expect
      .poll(async () => {
        const items = await webhookRequests()
        hit = items.find((i) => i.path === '/hook')
        return Boolean(hit)
      }, { timeout: 15_000 })
      .toBeTruthy()

    const ts = hit!.headers['x-backup-timestamp']
    const expectedSig = 'sha256=' + createHmac('sha256', secret).update(`${ts}.${hit!.body}`).digest('hex')
    expect(hit!.headers['x-backup-signature']).toBe(expectedSig)
    expect(hit!.headers['idempotency-key']).toMatch(/^[0-9a-f-]{36}$/)

    // Permanent 404 failure -> appears in platform failed deliveries -> flip sink to 200 -> retry -> Sent!
    const failName = uniq('hook-fail')
    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel('Tür').selectOption('webhook')
    await dialog.getByLabel('Ad', { exact: true }).fill(failName)
    await dialog.getByLabel('Web kancası adresi').fill('https://bps2-webhook/fail')
    await dialog.getByRole('button', { name: 'Kanal ekle' }).click()
    await expect(dialog).toBeHidden()
    await page.getByRole('button', { name: `Test gönder — ${failName}` }).click()
    await expect
      .poll(async () => (await webhookRequests()).some((i) => i.path === '/fail' && i.status === 404), { timeout: 15_000 })
      .toBeTruthy()

    await page.getByRole('tab', { name: 'Gönderim geçmişi' }).click()
    await expect(page.getByRole('listitem').filter({ hasText: failName }).first()).toContainText('Başarısız', { timeout: 15_000 })

    await signPlatform(page)
    await page.goto(url('/platform/notification-transports'))
    await page.getByLabel('Gönderim ara').fill(failName)
    const failedCard = page.getByRole('listitem').filter({ hasText: failName }).first()
    await expect(failedCard).toContainText('http_404')

    await setWebhookFailMode(false)
    await failedCard.getByRole('button', { name: 'Gönderimi yeniden dene' }).click()

    await expect
      .poll(async () => (await webhookRequests()).some((i) => i.path === '/fail' && i.status === 200), { timeout: 15_000 })
      .toBeTruthy()

    await signTenant(page)
    await page.goto(url(`/tenants/${t.tenantId}/settings?tab=deliveries`))
    await expect(page.getByRole('listitem').filter({ hasText: failName }).first()).toContainText('Gönderildi', { timeout: 15_000 })
    expect(problems).toEqual([])
  })

  test('SMS transport & channel (behind sms feature flag) -> validation, secret lifecycle, reload persistence & live Bearer delivery', async ({ page }) => {
    test.setTimeout(90_000)
    await clearWebhookSink()
    await signPlatform(page)
    const problems = watch(page, [
      { status: 400, path: /\/platform\/notification-transports\/sms$/ },
      { status: 400, path: /\/notifications\/channels$/ },
    ])

    await page.goto(url('/platform/notification-transports'))
    const smsSection = page.locator('section', { has: page.getByRole('heading', { name: 'SMS ağ geçidi' }) })
    await expect(smsSection).toBeVisible()
    const smsSave = smsSection.getByRole('button', { name: 'SMS ayarlarını kaydet' })
    await smsSection.getByRole('button', { name: 'Teknik ayrıntılar' }).click()

    // Empty endpoint blocks submit; non-https endpoint is rejected by server
    await smsSection.getByLabel('Uç nokta (https://…)').fill('')
    await expect(smsSave).toBeDisabled()
    await smsSection.getByLabel('Uç nokta (https://…)').fill('http://bps2-webhook/sms')
    await smsSave.click()
    await expect(smsSection.getByRole('alert')).toBeVisible()

    // Save disabled transport with Bearer auth first, set secret, then enable transport
    await smsSection.getByLabel('SMS aktarımı etkin').uncheck()
    await smsSection.getByLabel('Gönderen').fill('BackupApp')
    await smsSection.getByLabel('Uç nokta (https://…)').fill('https://bps2-webhook/sms')
    await smsSection.getByLabel('Kimlik doğrulama').selectOption('bearer')
    await smsSave.click()
    await expect(smsSection.getByText('SMS aktarımı kaydedildi.')).toBeVisible()

    await smsSection.getByLabel('Yeni Taşıyıcı kayıt anahtarı').fill('s2-sms-bearer-token')
    await smsSection.getByRole('button', { name: 'Ayarla', exact: true }).click()
    await expect(smsSection.getByText('Taşıyıcı kayıt anahtarı: kayıtlı. Kayıtlı değer geri okunamaz.')).toBeVisible()

    await smsSection.getByLabel('SMS aktarımı etkin').check()
    await smsSave.click()
    await expect(smsSection.getByText('Çalışıyor')).toBeVisible()

    await page.reload()
    const smsReloaded = page.locator('section', { has: page.getByRole('heading', { name: 'SMS ağ geçidi' }) })
    await expect(smsReloaded.getByLabel('Gönderen')).toHaveValue('BackupApp')
    await expect(smsReloaded.getByText('Taşıyıcı kayıt anahtarı: kayıtlı. Kayıtlı değer geri okunamaz.')).toBeVisible()

    // Clear secret (which disables transport server-side), then re-set secret and re-save enabled
    await smsReloaded.getByRole('button', { name: 'Temizle' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Gizli değeri temizle' }).click()
    await expect(smsReloaded.getByText('Taşıyıcı kayıt anahtarı: ayarlanmadı.')).toBeVisible()
    await smsReloaded.getByLabel('Yeni Taşıyıcı kayıt anahtarı').fill('s2-sms-bearer-token')
    await smsReloaded.getByRole('button', { name: 'Ayarla', exact: true }).click()
    await expect(smsReloaded.getByText('Taşıyıcı kayıt anahtarı: kayıtlı. Kayıtlı değer geri okunamaz.')).toBeVisible()
    await smsReloaded.getByLabel('SMS aktarımı etkin').check()
    await smsReloaded.getByRole('button', { name: 'SMS ayarlarını kaydet' }).click()
    await expect(smsReloaded.getByText('Çalışıyor')).toBeVisible()

    // Tenant validates phone format, creates SMS channel, and sends live test
    await signTenant(page)
    const smsName = uniq('sms')
    await page.goto(url(`/tenants/${t.tenantId}/settings?tab=channels`))
    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Tür').selectOption('sms')
    await dialog.getByLabel('Ad', { exact: true }).fill(smsName)
    await dialog.getByLabel('Telefon (E.164)').fill('05551112233')
    await dialog.getByRole('button', { name: 'Kanal ekle' }).click()
    await expect(dialog.getByRole('alert')).toBeVisible()

    await dialog.getByLabel('Telefon (E.164)').fill('+905551112233')
    await dialog.getByRole('button', { name: 'Kanal ekle' }).click()
    await expect(dialog).toBeHidden()

    await page.getByRole('button', { name: `Test gönder — ${smsName}` }).click()
    let smsHit: Awaited<ReturnType<typeof webhookRequests>>[number] | undefined
    await expect
      .poll(async () => {
        const items = await webhookRequests()
        smsHit = items.find((i) => i.path === '/sms')
        return Boolean(smsHit)
      }, { timeout: 15_000 })
      .toBeTruthy()
    expect(smsHit!.headers['authorization']).toBe('Bearer s2-sms-bearer-token')

    await page.getByRole('tab', { name: 'Gönderim geçmişi' }).click()
    await expect(page.getByRole('listitem').filter({ hasText: smsName }).first()).toContainText('Gönderildi', { timeout: 15_000 })
    expect(problems).toEqual([])
  })
})
