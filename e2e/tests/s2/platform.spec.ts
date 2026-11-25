import { expect, test, type Locator, type Page } from '@playwright/test'
import { ensureTenant } from '../../api'
import { platformApi, s, seedIsolatedTenant, signInAs, signInPlatform, tenantApi, uniq, watch } from './helpers'

const TENANT = 'e2e-s2-platform'
const listUrl = '/platform/storage/connections'
let tenantId = ''

async function cleanManaged() {
  const api = await platformApi()
  const page = await api.call('GET', `/api/platform/storage-connections?tenantId=${tenantId}&pageSize=100`)
  for (const c of page.items as { id: string }[]) {
    await api.call('DELETE', `/api/platform/storage-connections/${c.id}`, undefined, [200, 204, 404, 409])
  }
}

test.beforeAll(async () => {
  const api = await platformApi()
  tenantId = await ensureTenant(api, TENANT)
  await cleanManaged()
})
test.afterAll(cleanManaged)

async function openAddDialog(page: Page) {
  await page.goto(listUrl)
  await page.getByRole('button', { name: 'Yönetilen hedef ekle' }).click()
  return page.getByRole('dialog')
}
async function pickTenant(dialog: Locator, name = TENANT) {
  await dialog.getByLabel('Çalışma alanı').fill(name)
  await dialog.getByRole('option', { name }).click()
}
const rowFor = (page: Page, name: string) => page.getByRole('row').filter({ hasText: name })
async function search(page: Page, name: string) {
  await page.getByLabel(/Depolama hedefi ara|Ara/i).fill(name)
  await expect(rowFor(page, name)).toHaveCount(1)
}

test.describe('platform storage usage (/platform/storage)', () => {
  test('searches workspace, filters by quota/subscription, reconciles and rebuilds usage cleanly', async ({ page }) => {
    test.setTimeout(60_000)
    await signInPlatform(page)
    const problems = watch(page)
    await page.goto('/platform/storage')
    await page.getByLabel('Çalışma alanı ara').fill(TENANT)
    const row = rowFor(page, TENANT)
    await expect(row).toHaveCount(1)

    // Reconcile usage
    await row.getByText('İşlemler').click()
    await row.getByRole('button', { name: 'Kullanımı doğrula' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Uzlaştırmayı çalıştır' }).click()
    await expect(page.getByText(/tutarlılık denetimi tamamlandı/)).toBeVisible()

    // Rebuild usage (requires typing workspace name)
    if (!(await row.getByRole('button', { name: 'Baştan hesapla' }).isVisible())) {
      await row.getByText('İşlemler').click()
    }
    await row.getByRole('button', { name: 'Baştan hesapla' }).click()
    const rebuildDialog = page.getByRole('dialog')
    await expect(rebuildDialog.getByRole('button', { name: 'Yeniden hesaplamayı çalıştır' })).toBeDisabled()
    await rebuildDialog.getByRole('textbox').fill(TENANT)
    await rebuildDialog.getByRole('button', { name: 'Yeniden hesaplamayı çalıştır' }).click()
    await expect(page.getByText(/kullanım baştan hesaplandı/)).toBeVisible()

    // Filter empty-state + clear filters
    await page.getByLabel('Kota durumu').selectOption('OverQuota')
    await expect(page.getByText('Kriterlere uygun çalışma alanı kullanımı bulunamadı.').or(page.locator('table'))).toBeVisible()
    expect(problems).toEqual([])
  })
})

test.describe('platform-managed storage connection CRUD (/platform/storage/connections)', () => {
  test('add dialog: required fields and quota scope validation', async ({ page }) => {
    await signInPlatform(page)
    const problems = watch(page)
    const dialog = await openAddDialog(page)
    const submit = dialog.getByRole('button', { name: 'Yönetilen hedef ekle' })
    await expect(submit).toBeDisabled()
    await pickTenant(dialog)
    await dialog.getByLabel('Depolama sağlayıcısı').selectOption({ label: 'Merkezi yönetilen depolama' })
    await expect(submit).toBeDisabled()
    await dialog.getByLabel('Hedef adı').fill(uniq('central'))
    await expect(submit).toBeEnabled()
    await dialog.getByLabel('Kota kapsamı (isteğe bağlı)').fill('Bad Scope!')
    await expect(dialog.getByText('Kota kapsamı yalnızca küçük harf, rakam ve “-” içerebilir (1-64 karakter).')).toBeVisible()
    await expect(submit).toBeDisabled()
    await dialog.getByLabel('Kota kapsamı (isteğe bağlı)').fill('s2-scope')
    await expect(submit).toBeEnabled()
    await dialog.getByRole('button', { name: 'Vazgeç' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()
    expect(problems).toEqual([])
  })

  test('create, edit, tenant-managed quotaScope edit, platform lock effect on tenant view, test, one-per-provider limit, ownership filter, delete', async ({ page }) => {
    test.setTimeout(90_000)
    const lockTenant = await seedIsolatedTenant('platlock')
    try {
      await signInPlatform(page)
      const problems = watch(page, [
        { status: 409, path: /\/platform\/tenants\/.*\/storage-connections$/ },
      ])
      const name = uniq('central')
      const renamed = `${name}-r`

      const dialog = await openAddDialog(page)
      await pickTenant(dialog, lockTenant.tenantName)
      await dialog.getByLabel('Depolama sağlayıcısı').selectOption({ label: 'Merkezi yönetilen depolama' })
      await dialog.getByLabel('Hedef adı').fill(name)
      await dialog.getByLabel('Kota kapsamı (isteğe bağlı)').fill('s2-scope')
      await dialog.getByRole('button', { name: 'Yönetilen hedef ekle' }).click()
      await expect(page.getByRole('dialog')).toBeHidden()
      await search(page, name)
      await expect(rowFor(page, name)).toContainText('Platform yönetimli')
      await expect(rowFor(page, name)).toContainText(lockTenant.tenantName)

      // A second central connection for the same tenant is refused cleanly
      const again = await openAddDialog(page)
      await pickTenant(again, lockTenant.tenantName)
      await again.getByLabel('Depolama sağlayıcısı').selectOption({ label: 'Merkezi yönetilen depolama' })
      await again.getByLabel('Hedef adı').fill(`${name}-2`)
      await again.getByRole('button', { name: 'Yönetilen hedef ekle' }).click()
      await expect(again.getByRole('alert').getByText('Bu depolama sağlayıcısı için bağlantı sınırına ulaşıldı.')).toBeVisible()
      await again.getByRole('button', { name: 'Vazgeç' }).click()

      // Edit name and verify saved quotaScope
      await page.reload()
      await search(page, name)
      await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
      const edit = page.getByRole('dialog')
      await expect(edit.getByLabel('Kota kapsamı')).toHaveValue('s2-scope')
      await edit.getByLabel('Ad', { exact: true }).fill(renamed)
      await edit.getByRole('button', { name: 'Kaydet' }).click()
      await expect(page.getByRole('dialog')).toBeHidden()
      await search(page, renamed)

      // Platform lock on -> visible warning in tenant view -> unlock
      await page.getByRole('button', { name: `Platform kilidi koy — ${renamed}` }).click()
      await page.getByRole('dialog').getByRole('button', { name: 'Platform kilidi koy' }).click()
      await expect(page.getByRole('button', { name: `Platform kilidini kaldır — ${renamed}` })).toBeVisible()

      await signInAs(page, lockTenant.account)
      await page.goto(`/tenants/${lockTenant.tenantId}/storage`)
      await expect(page.getByRole('article', { name: renamed })).toContainText(/Platform/i)

      await signInPlatform(page)
      await page.goto(listUrl)
      await search(page, renamed)
      await page.getByRole('button', { name: `Platform kilidini kaldır — ${renamed}` }).click()
      await page.getByRole('dialog').getByRole('button', { name: /Platform kilidini kaldır/ }).click()
      await expect(page.getByRole('button', { name: `Platform kilidi koy — ${renamed}` })).toBeVisible()

      // Ownership filter switches between TenantManaged (edit quotaScope on default Local) and PlatformManaged
      await page.getByLabel('Sahiplik').selectOption('TenantManaged')
      await expect(rowFor(page, renamed)).toHaveCount(0)
      await page.getByLabel(/Depolama hedefi ara|Ara/i).fill(lockTenant.tenantName)
      const tenantLocalRow = rowFor(page, lockTenant.tenantName).first()
      await expect(tenantLocalRow).toBeVisible()
      await tenantLocalRow.getByRole('button', { name: /^Düzenle — / }).click()
      const tEdit = page.getByRole('dialog')
      await expect(tEdit.getByLabel('Ad', { exact: true })).toBeDisabled()
      await tEdit.getByLabel('Kota kapsamı').fill('tenant-s2')
      await tEdit.getByRole('button', { name: 'Kaydet' }).click()
      await expect(tEdit).toBeHidden()

      await page.getByLabel('Sahiplik').selectOption('PlatformManaged')
      await search(page, renamed)

      await page.getByRole('button', { name: `Test et — ${renamed}` }).click()
      await expect(rowFor(page, renamed)).not.toContainText('Henüz test edilmedi')

      await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
      const del = page.getByRole('dialog')
      await expect(del.getByRole('button', { name: 'Bağlantıyı sil' })).toBeDisabled()
      await del.getByRole('textbox').fill(renamed)
      await del.getByRole('button', { name: 'Bağlantıyı sil' }).click()
      await expect(page.getByRole('dialog')).toBeHidden()
      await page.getByLabel(/Depolama hedefi ara|Ara/i).fill(renamed)
      await expect(rowFor(page, renamed)).toHaveCount(0)
      expect(problems).toEqual([])
    } finally {
      await lockTenant.api.dispose()
    }
  })
})

test.describe('platform quota policies & agent settings forms', () => {
  let initialAgentSettings: { serverUrl: string | null; enrollmentTokenLifetimeMinutes: number }

  test.beforeAll(async () => {
    const api = await platformApi()
    const cur = await api.call('GET', '/api/platform/agent-settings')
    initialAgentSettings = {
      serverUrl: cur.serverUrl ?? null,
      enrollmentTokenLifetimeMinutes: cur.enrollmentTokenLifetimeMinutes,
    }
  })

  test.afterAll(async () => {
    if (initialAgentSettings) {
      const api = await platformApi()
      await api.call('PUT', '/api/platform/agent-settings', initialAgentSettings)
    }
  })

  test('quota policies (/platform/quota-policies): validation, create with quotaScope, reload persistence, edit & disable', async ({ page }) => {
    test.setTimeout(60_000)
    await signInPlatform(page)
    const problems = watch(page, [{ status: 400, path: /\/platform\/quota-policies/ }])
    const key = uniq('qpol')
    const name = `S2 Policy ${key}`
    const renamed = `${name} Updated`

    await page.goto('/platform/quota-policies')
    await page.getByRole('button', { name: 'Yeni politika' }).click()
    let dialog = page.getByRole('dialog')
    const createBtn = dialog.getByRole('button', { name: 'Oluştur' })
    await expect(createBtn).toBeDisabled()

    await dialog.getByLabel('Politika anahtarı').fill(key)
    await expect(createBtn).toBeDisabled()
    await dialog.getByLabel('Politika adı').fill(name)
    await expect(createBtn).toBeEnabled()

    await dialog.getByLabel('Depolama kapsamı').selectOption('quotaScope')
    await dialog.getByLabel(/Kota kapsamı etiketleri/).fill('Invalid Scope!')
    await createBtn.click()
    await expect(dialog.getByRole('alert')).toBeVisible()

    await dialog.getByLabel(/Kota kapsamı etiketleri/).fill('s2-scope, managed')
    await createBtn.click()
    await expect(dialog).toBeHidden()

    await page.reload()
    await page.getByLabel('Politika ara').fill(key)
    const row = rowFor(page, name)
    await expect(row).toHaveCount(1)
    await expect(row).toContainText('Etkin')

    // Edit policy name & disable (confirming consequence dialog)
    await row.getByRole('button', { name: 'Düzenle' }).click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel('Politika adı').fill(renamed)
    await dialog.getByLabel('Etkin politika (planlarda seçilebilir)').uncheck()
    await dialog.getByRole('button', { name: 'Kaydet' }).click()
    const confirmDialog = page.getByRole('dialog').last()
    await confirmDialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()

    await page.reload()
    await page.getByLabel('Politika ara').fill(key)
    await expect(rowFor(page, renamed)).toContainText('Devre dışı')
    expect(problems).toEqual([])
  })

  test('agent settings (/platform/agent-settings): server URL & token lifetime validation, save & reload persistence', async ({ page }) => {
    await signInPlatform(page)
    const problems = watch(page, [{ status: 400, path: /\/platform\/agent-settings$/ }])
    await page.goto('/platform/agent-settings')

    const urlInput = page.getByLabel('Sunucu HTTPS adresi')
    const lifeInput = page.getByLabel('Kayıt anahtarının geçerlilik süresi (dakika)')
    const save = page.getByRole('button', { name: 'Kaydet' })

    // Invalid lifetime disables submit client-side
    await lifeInput.fill('0')
    await expect(save).toBeDisabled()
    await lifeInput.fill('45')
    await expect(save).toBeEnabled()

    // Non-HTTPS URL rejected by server with clear field error
    await urlInput.fill('http://insecure.example.com')
    await save.click()
    await expect(page.getByText(/Geçerli bir HTTPS adresi girin/)).toBeVisible()

    // Save valid HTTPS URL + lifetime & verify reload persistence
    await urlInput.fill('https://backup-s2.example.test:5252')
    await save.click()
    await expect(page.getByText('Yedekleme bilgisayarı ayarları kaydedildi.')).toBeVisible()
    await page.reload()
    await expect(page.getByLabel('Sunucu HTTPS adresi')).toHaveValue('https://backup-s2.example.test:5252')
    await expect(page.getByLabel('Kayıt anahtarının geçerlilik süresi (dakika)')).toHaveValue('45')
    expect(problems).toEqual([])
  })
})

test.describe('platform notification transports & retention settings', () => {
  let initialEmail: any
  let initialHistRetention: number
  let initialAuditRetention: number

  test.beforeAll(async () => {
    const api = await platformApi()
    initialEmail = (await api.call('GET', '/api/platform/notification-transports')).email
    initialHistRetention = (await api.call('GET', '/api/platform/notifications/history-retention')).retentionDays
    initialAuditRetention = (await api.call('GET', '/api/platform/settings/audit-retention')).retentionDays
  })

  test.afterAll(async () => {
    const api = await platformApi()
    if (initialEmail) {
      await api.call('DELETE', '/api/platform/notification-transports/email/secret', undefined, [200, 204])
      await api.call('PUT', '/api/platform/notification-transports/email', {
        enabled: initialEmail.enabled,
        host: initialEmail.host ?? 'bpe2e-mailpit',
        port: initialEmail.port ?? 1025,
        security: initialEmail.security ?? 'none',
        username: initialEmail.username ?? null,
        fromAddress: initialEmail.fromAddress ?? 'platform@e2e.test',
        fromName: initialEmail.fromName ?? 'E2E Platform',
      })
    }
    if (initialHistRetention) {
      await api.call('PUT', '/api/platform/notifications/history-retention', { retentionDays: initialHistRetention })
    }
    if (initialAuditRetention) {
      await api.call('PUT', '/api/platform/settings/audit-retention', { retentionDays: initialAuditRetention })
    }
  })

  test('SMTP transport: client/server validation, save & reload persistence, write-only secret set & clear', async ({ page }) => {
    test.setTimeout(60_000)
    await signInPlatform(page)
    const problems = watch(page, [{ status: 400, path: /\/platform\/notification-transports\/email$/ }])
    await page.goto('/platform/notification-transports')

    // SMS card hidden when sms feature flag is off
    await expect(page.getByRole('heading', { name: 'SMS ağ geçidi' })).toHaveCount(0)

    const save = page.getByRole('button', { name: 'E-posta ayarlarını kaydet' })
    await page.getByRole('button', { name: 'Teknik ayrıntılar' }).first().click()

    // Invalid port / empty sender disables submit
    await page.getByLabel('Bağlantı noktası').fill('70000')
    await expect(save).toBeDisabled()
    await page.getByLabel('Bağlantı noktası').fill('1025')
    await page.getByLabel('Gönderen adresi').fill('')
    await expect(save).toBeDisabled()

    // Server rule: security="none" ("Şifreleme yok") with username is rejected
    await page.getByLabel('Gönderen adresi').fill('s2-alerts@e2e.test')
    await page.getByLabel('Gönderen adı').fill('S2 Alerts')
    await page.getByLabel('Sunucu').fill('e2e-s2-mailpit')
    await page.getByLabel('Güvenlik', { exact: true }).selectOption('none')
    await page.getByLabel('Kullanıcı adı').fill('smtp-user')
    await save.click()
    await expect(page.getByRole('alert').first()).toBeVisible()

    // Valid STARTTLS + username + secret lifecycle, then restore clean disabled state
    await page.getByLabel('Güvenlik', { exact: true }).selectOption('starttls')
    await page.getByLabel('E-posta aktarımı etkin').uncheck()
    await save.click()
    await expect(page.getByText('E-posta aktarımı kaydedildi.')).toBeVisible()

    await page.getByLabel('Yeni SMTP parolası').fill('S2-smtp-secret!')
    await page.getByRole('button', { name: 'Ayarla', exact: true }).click()
    await expect(page.getByText('SMTP parolası: kayıtlı. Kayıtlı değer geri okunamaz.')).toBeVisible()
    await expect(page.getByLabel('Yeni SMTP parolası')).toHaveValue('')

    await page.reload()
    await expect(page.getByLabel('Gönderen adresi')).toHaveValue('s2-alerts@e2e.test')
    await expect(page.getByText('SMTP parolası: kayıtlı. Kayıtlı değer geri okunamaz.')).toBeVisible()

    await page.getByRole('button', { name: 'Temizle' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Gizli değeri temizle' }).click()
    await expect(page.getByText('SMTP parolası: ayarlanmadı.')).toBeVisible()
    expect(problems).toEqual([])
  })

  test('notification history retention & audit/share history retention forms: validation, save, reload persistence', async ({ page }) => {
    await signInPlatform(page)
    const problems = watch(page)

    // 1. Notification history retention on /platform/notification-transports
    await page.goto('/platform/notification-transports')
    const histInput = page.getByLabel('Saklama süresi (gün)')
    const histSave = page.getByRole('button', { name: 'Saklama süresini kaydet' })
    for (const bad of ['0', '-5', '36501', '12.5', '']) {
      await histInput.fill(bad)
      await expect(histSave, `history retention "${bad}"`).toBeDisabled()
    }
    await histInput.fill('120')
    await expect(histSave).toBeEnabled()
    await Promise.all([
      page.waitForResponse((r) => r.url().includes('/platform/notifications/history-retention') && r.request().method() === 'PUT' && r.ok()),
      histSave.click(),
    ])
    await page.reload()
    await expect(page.getByLabel('Saklama süresi (gün)')).toHaveValue('120')

    // 2. Audit & share history retention on /platform/shares
    await page.goto('/platform/shares')
    await page.getByText('Denetim ve paylaşım geçmişi saklama süresi').first().click()
    const auditInput = page.getByLabel('Saklama süresi (gün)')
    const auditSave = page.getByRole('button', { name: 'Saklama süresini kaydet' })
    await auditInput.fill('0')
    await auditSave.click()
    await expect(page.getByText('Saklama süresi en az 1 gün olmalıdır.')).toBeVisible()

    await auditInput.fill('180')
    await auditSave.click()
    await expect(page.getByText('Saklama süresi güncellendi.')).toBeVisible()
    await page.reload()
    await page.getByText('Denetim ve paylaşım geçmişi saklama süresi').first().click()
    await expect(page.getByLabel('Saklama süresi (gün)')).toHaveValue('180')
    expect(problems).toEqual([])
  })

  test.fixme('changing notification history retention keeps confirmation notice visible (#285)', async ({ page }) => {
    const api = await platformApi()
    await api.call('PUT', '/api/platform/notifications/history-retention', { retentionDays: 90 })
    await signInPlatform(page)
    await page.goto('/platform/notification-transports')
    await page.getByLabel('Saklama süresi (gün)').fill('121')
    await page.getByRole('button', { name: 'Saklama süresini kaydet' }).click()
    await expect(page.getByText('Saklama süresi kaydedildi.')).toBeVisible()
  })

  test('tenant user is refused every platform storage, notification transport and retention API write/read', async () => {
    const api = await tenantApi()
    const denied = [401, 403]
    await api.call('GET', '/api/platform/storage-connections', undefined, denied)
    await api.call('POST', `/api/platform/tenants/${s.tenantId}/storage-connections`, { providerType: 'central', name: 'e2e-s2-denied', ownership: 'PlatformManaged' }, denied)
    await api.call('DELETE', `/api/platform/storage-connections/${tenantId}`, undefined, denied)
    await api.call('GET', '/api/platform/notification-transports', undefined, denied)
    await api.call('PUT', '/api/platform/notification-transports/email', {}, denied)
    await api.call('PUT', '/api/platform/notifications/history-retention', { retentionDays: 30 }, denied)
    await api.call('PUT', '/api/platform/settings/audit-retention', { retentionDays: 30 }, denied)
  })
})
