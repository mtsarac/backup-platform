import { generateKeyPairSync } from 'node:crypto'
import { expect, test, type Page } from '@playwright/test'
import { enableAgentEnrollment } from '../../api'
import { createJob, enrollAgent } from '../../protocol-agent'
import { axeViolations, platformApi, seedIsolatedTenant, signInAs, uniq, watch, type IsolatedTenant } from './helpers'

let t: IsolatedTenant
let settingsUrl: string
let channelsApi: string

async function resetSettings() {
  const api = t.api
  const list = await api.call('GET', channelsApi)
  for (const c of list.items as { id: string; name: string }[]) {
    if (c.name.startsWith('e2e-s2-')) await api.call('DELETE', `${channelsApi}/${c.id}`, undefined, [200, 204, 404])
  }
  const profiles = (await api.call('GET', `/api/tenants/${t.tenantId}/bandwidth-profiles`)) as { id: string; name: string }[]
  await api.call('PUT', `/api/tenants/${t.tenantId}/upload-bandwidth/tenant`, { bytesPerSecond: null })
  await api.call('PUT', `/api/tenants/${t.tenantId}/upload-bandwidth/tenant/profile`, { profileId: null }, [200, 204])
  for (const p of profiles) {
    if (p.name.startsWith('e2e-s2-')) await api.call('DELETE', `/api/tenants/${t.tenantId}/bandwidth-profiles/${p.id}`, undefined, [200, 204, 404])
  }
  await api.call('DELETE', `/api/tenants/${t.tenantId}/encryption/recovery-key`, undefined, [200, 204, 404])
}

test.beforeAll(async () => {
  t = await seedIsolatedTenant('settings')
  await enableAgentEnrollment(await platformApi(), t.tenantId)
  settingsUrl = `/tenants/${t.tenantId}/settings`
  channelsApi = `/api/tenants/${t.tenantId}/notifications/channels`
  await resetSettings()
})

test.afterAll(async () => {
  if (t) {
    try { await resetSettings() } finally { await t.api.dispose() }
  }
})

const publicKeyPem = (bits: number) =>
  generateKeyPairSync('rsa', { modulusLength: bits }).publicKey.export({ type: 'spki', format: 'pem' }).toString()

async function openTab(page: Page, name: string) {
  await page.goto(settingsUrl)
  await page.getByRole('tab', { name }).click()
}
const card = (page: Page, name: string) => page.getByRole('article', { name })

test.describe('in-app notifications (top-bar bell)', () => {
  test('shows unread security notification from initial password change, marks as read, persists across reload', async ({ page }) => {
    const fresh = await seedIsolatedTenant('bell')
    try {
      await signInAs(page, fresh.account)
      const problems = watch(page)
      await page.goto(`/tenants/${fresh.tenantId}/settings`)
      const bell = page.getByRole('button', { name: /Bildirimler\s*\(1\)/ })
      await expect(bell).toBeVisible()
      await bell.click()

      const panel = page.locator('#in-app-notifications')
      await expect(panel).toBeVisible()
      await expect(panel).toContainText('Hesap güvenliği')
      await expect(panel).toContainText('Zorunlu güvenlik bildirimi')
      expect(await axeViolations(page)).toEqual([])

      await panel.getByRole('button', { name: 'Okundu olarak işaretle' }).click()
      await expect(panel.getByText('Okundu', { exact: true })).toBeVisible()
      await expect(page.getByRole('button', { name: /Bildirimler\s*\(/ })).toHaveCount(0)

      await page.reload()
      await page.getByRole('button', { name: 'Bildirimler', exact: true }).click()
      await expect(page.locator('#in-app-notifications').getByText('Okundu', { exact: true })).toBeVisible()
      expect(problems).toEqual([])
    } finally {
      await fresh.api.dispose()
    }
  })
})

test.describe('notification policy', () => {
  test('save persists across reload; every switch can be turned back and operated with keyboard', async ({ page }) => {
    await signInAs(page, t.account)
    const problems = watch(page)
    await page.goto(settingsUrl)
    await expect(page.getByText(/E-posta ve web kancası ile uyarı bildirimleri\./)).toBeVisible()

    const enabled = page.getByRole('switch', { name: 'Bildirimler etkin' })
    const recovery = page.getByRole('checkbox', { name: 'Düzelme bildirimleri gönder' })
    await enabled.uncheck()
    await recovery.uncheck()
    await page.getByRole('button', { name: 'Politikayı kaydet' }).click()
    await expect(page.getByText('Politika kaydedildi.')).toBeVisible()
    await expect(page.getByText('Henüz kaydedilmedi')).toBeHidden()
    await page.reload()
    await expect(enabled).not.toBeChecked()
    await expect(recovery).not.toBeChecked()

    await enabled.focus()
    await page.keyboard.press('Space')
    await expect(enabled).toBeChecked()
    await recovery.check()
    await page.getByRole('button', { name: 'Politikayı kaydet' }).click()
    await expect(page.getByText('Politika kaydedildi.')).toBeVisible()
    await page.reload()
    await expect(enabled).toBeChecked()
    await expect(recovery).toBeChecked()
    expect(problems).toEqual([])
  })
})

test.describe('notification channels & deliveries history', () => {
  test('add dialog: submit needs name and target; rejects malformed email, non-https webhook and non-443 port; SMS hidden when flag off', async ({ page }) => {
    await signInAs(page, t.account)
    const problems = watch(page, [{ status: 400, path: /\/notifications\/channels$/ }])
    await openTab(page, 'Kanallar')
    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    const dialog = page.getByRole('dialog')
    await expect(dialog.getByLabel('Tür').locator('option', { hasText: /SMS/i })).toHaveCount(0)

    const submit = dialog.getByRole('button', { name: 'Kanal ekle' })
    await expect(submit).toBeDisabled()
    await dialog.getByLabel('Ad', { exact: true }).fill(uniq('bad-mail'))
    await expect(submit).toBeDisabled()
    await dialog.getByLabel('E-posta adresi').fill('not-an-address')
    await expect(submit).toBeEnabled()
    await submit.click()
    await expect(dialog.getByRole('alert')).toBeVisible()

    await dialog.getByLabel('Tür').selectOption('webhook')
    await dialog.getByLabel('Web kancası adresi').fill('http://example.com/hook')
    await submit.click()
    await expect(dialog.getByRole('alert')).toBeVisible()

    await dialog.getByLabel('Web kancası adresi').fill('https://example.com:8443/hook')
    await submit.click()
    await expect(dialog.getByRole('alert')).toBeVisible()

    await dialog.getByRole('button', { name: 'Vazgeç' }).click()
    await expect(dialog).toBeHidden()
    expect(problems).toEqual([])
  })

  test('email channel: create, unconfirmed state, edit name & target, transport-off error on resend, disable/enable, delete', async ({ page }) => {
    test.setTimeout(90_000)
    await signInAs(page, t.account)
    const problems = watch(page, [{ status: 409, path: /\/notifications\/channels\/.*\/test$/ }])
    const name = uniq('mail')
    const renamed = `${name}-r`

    await openTab(page, 'Kanallar')
    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    let dialog = page.getByRole('dialog')
    await dialog.getByLabel('Ad', { exact: true }).fill(name)
    await dialog.getByLabel('E-posta adresi').fill('e2e-s2@example.test')
    await dialog.getByRole('button', { name: 'Kanal ekle' }).click()
    await expect(dialog).toBeHidden()
    await expect(card(page, name)).toContainText('Adres onayı bekleniyor')
    await expect(card(page, name)).toContainText('Henüz gönderim yok')

    await page.reload()
    await page.getByRole('tab', { name: 'Kanallar' }).click()
    await expect(card(page, name)).toBeVisible()

    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel('Ad', { exact: true }).fill(renamed)
    await dialog.getByLabel('E-posta adresi').fill('e2e-s2-updated@example.test')
    await dialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog).toBeHidden()
    await expect(card(page, renamed)).toBeVisible()

    await page.getByRole('button', { name: `Onay isteğini yeniden gönder — ${renamed}` }).click()
    await expect(card(page, renamed)).toContainText('Bu kanal türü için platform aktarımı yapılandırılmamış. Platform yöneticisine başvurun.')

    await card(page, renamed).getByRole('button', { name: 'Teknik ayrıntılar' }).first().click()
    await page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` }).click()
    await expect(page.getByRole('button', { name: `Etkinleştir — ${renamed}` })).toBeVisible()
    await page.reload()
    await page.getByRole('tab', { name: 'Kanallar' }).click()
    await card(page, renamed).getByRole('button', { name: 'Teknik ayrıntılar' }).click()
    await page.getByRole('button', { name: `Etkinleştir — ${renamed}` }).click()
    await expect(page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` })).toBeVisible()

    await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
    dialog = page.getByRole('dialog')
    await expect(dialog.getByRole('button', { name: 'Kanalı sil' })).toBeDisabled()
    await dialog.getByRole('textbox').fill(renamed)
    await dialog.getByRole('button', { name: 'Kanalı sil' }).click()
    await expect(dialog).toBeHidden()
    await expect(card(page, renamed)).toHaveCount(0)
    expect(problems).toEqual([])
  })

  test('webhook channel: secret lifecycle, test send, deliveries history search/filters/persistence after channel deletion', async ({ page }) => {
    test.setTimeout(90_000)
    await signInAs(page, t.account)
    const problems = watch(page)
    const name = uniq('hook')

    await openTab(page, 'Kanallar')
    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    let dialog = page.getByRole('dialog')
    await dialog.getByLabel('Tür').selectOption('webhook')
    await dialog.getByLabel('Ad', { exact: true }).fill(name)
    await dialog.getByLabel('Web kancası adresi').fill('https://hooks.example.com/e2e-s2')
    await dialog.getByLabel('Gizli anahtar').fill('s2-hook-secret')
    await dialog.getByRole('button', { name: 'Kanal ekle' }).click()
    await expect(dialog).toBeHidden()
    await expect(card(page, name)).toContainText('Web Kancası')

    await card(page, name).getByRole('button', { name: 'Teknik ayrıntılar' }).click()
    await expect(card(page, name)).toContainText('Gizli anahtar kayıtlı')
    await page.getByRole('button', { name: `Gizli anahtarı temizle — ${name}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Gizli anahtarı temizle' }).click()
    await expect(card(page, name)).toContainText('Gizli anahtar ayarlanmadı')

    // Re-add secret via Edit dialog
    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel('Gizli anahtar').fill('s2-hook-secret-2')
    await dialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog).toBeHidden()
    await expect(card(page, name)).toContainText('Gizli anahtar kayıtlı')

    await page.getByRole('button', { name: `Test gönder — ${name}` }).click()
    await expect(page.getByText(/Test bildirimi kuyruğa alındı/)).toBeVisible()

    // Deliveries screen: search, filters, technical details
    await page.getByRole('tab', { name: 'Gönderim geçmişi' }).click()
    const item = page.getByRole('listitem').filter({ hasText: name }).first()
    await expect(item).toContainText('Test bildirimi')
    await page.getByLabel('Gönderim ara').fill(name)
    await expect(item).toBeVisible()
    await page.getByLabel('Kanal türü', { exact: true }).selectOption('webhook')
    await page.getByLabel('Olay türü', { exact: true }).selectOption('test')
    await expect(item).toBeVisible()
    await item.getByRole('button', { name: 'Teknik ayrıntılar' }).click()
    await expect(item).toContainText('Gönderim kimliği')

    // Deleting the channel preserves its delivery history record
    await page.getByRole('tab', { name: 'Kanallar' }).click()
    await card(page, name).getByRole('button', { name: 'Teknik ayrıntılar' }).click()
    await page.getByRole('button', { name: `Sil — ${name}` }).click()
    dialog = page.getByRole('dialog')
    await dialog.getByRole('textbox').fill(name)
    await dialog.getByRole('button', { name: 'Kanalı sil' }).click()
    await expect(dialog).toBeHidden()

    await page.getByRole('tab', { name: 'Gönderim geçmişi' }).click()
    await expect(page.getByRole('listitem').filter({ hasText: name }).first()).toBeVisible()
    expect(problems).toEqual([])
  })

  test('populated /settings?tab=deliveries passes axe definition-list rule (#283)', async ({ page }) => {
    await signInAs(page, t.account)
    await page.goto(`${settingsUrl}?tab=deliveries`)
    await expect(page.getByRole('listitem').first()).toBeVisible()
    expect(await axeViolations(page)).toEqual([])
  })
})

test.describe('upload bandwidth (static limits, profiles, per-agent & per-job limits)', () => {
  test('static limit validation, save, persistence after reload, back to unlimited', async ({ page }) => {
    await signInAs(page, t.account)
    const problems = watch(page)
    await page.goto(`${settingsUrl}/bandwidth`)
    await page.getByRole('button', { name: `Düzenle — ${t.tenantName}` }).click()
    const dialog = page.getByRole('dialog')
    const save = dialog.getByRole('button', { name: 'Kaydet' })
    await dialog.getByLabel('Sınırsız (sınır yok)').uncheck()
    for (const bad of ['0,1', '800001', 'abc', '']) {
      await dialog.getByLabel('Hız sınırı').fill(bad)
      await expect(save, `limit "${bad}"`).toBeDisabled()
    }
    await dialog.getByLabel('Hız sınırı').fill('50')
    await expect(save).toBeEnabled()
    await save.click()
    await expect(dialog).toBeHidden()
    await expect(page.getByTestId('tenant-limit')).toContainText('50 Mbit/sn')
    await page.reload()
    await expect(page.getByTestId('tenant-limit')).toContainText('50 Mbit/sn')

    await page.getByRole('button', { name: `Düzenle — ${t.tenantName}` }).click()
    await page.getByRole('dialog').getByLabel('Sınırsız (sınır yok)').check()
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()
    await expect(page.getByTestId('tenant-limit')).toContainText('Sınırsız')
    await page.reload()
    await expect(page.getByTestId('tenant-limit')).toContainText('Sınırsız')
    expect(problems).toEqual([])
  })

  test('bandwidth profiles CRUD (rate window + pause window), tenant assignment, reload persistence, and per-agent/per-job limits', async ({ page }) => {
    test.setTimeout(90_000)
    await signInAs(page, t.account)
    const problems = watch(page)
    const profName = uniq('prof')
    const profRenamed = `${profName}-r`

    // First verify empty states on agents and jobs tabs before seeding protocol agent/job
    await page.goto(`${settingsUrl}/bandwidth?tab=agents`)
    await expect(page.getByText('Yedekleme bilgisayarı bulunamadı.')).toBeVisible()
    await page.getByRole('tab', { name: 'Yedekleme işleri' }).click()
    await expect(page.getByText('Yedekleme işi bulunamadı.')).toBeVisible()
    await page.getByText('Hangi sınır geçerli?').click()
    await expect(page.getByText(/Sınırlanmayanlar:/)).toBeVisible()

    // Profiles CRUD
    await page.getByRole('tab', { name: 'Profiller' }).click()
    await expect(page.getByText(/Henüz profil yok/)).toBeVisible()
    await page.getByRole('button', { name: 'Yeni profil' }).click()
    let dialog = page.getByRole('dialog')
    await expect(dialog.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    await dialog.getByLabel('Profil adı').fill(profName)

    // Identical start and end time blocks adding window
    await dialog.getByLabel('Başlangıç').fill('09:00')
    await dialog.getByLabel('Bitiş').fill('09:00')
    await expect(dialog.getByRole('button', { name: /Pencereyi listeye ekle/i })).toBeDisabled()

    // Add rate-limited window (Monday 09:00-18:00, 25 Mbit/sn)
    await dialog.getByLabel('Bitiş').fill('18:00')
    await dialog.getByLabel('Hız limiti').fill('25')
    await dialog.getByRole('button', { name: /Pencereyi listeye ekle/i }).click()
    await expect(dialog.getByText(/Tanımlı pencereler \(1\)/i)).toBeVisible()

    // Add pause window (Saturday 12:00-13:00)
    await dialog.getByLabel('Pencere davranışı').selectOption('pause')
    await dialog.getByLabel('Gün').selectOption('6')
    await dialog.getByLabel('Başlangıç').fill('12:00')
    await dialog.getByLabel('Bitiş').fill('13:00')
    await dialog.getByRole('button', { name: /Pencereyi listeye ekle/i }).click()
    await expect(dialog.getByText(/Tanımlı pencereler \(2\)/i)).toBeVisible()

    await dialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog).toBeHidden()
    await expect(page.getByRole('heading', { name: profName })).toBeVisible()
    await expect(page.getByText(/Aktarımı duraklat/)).toBeVisible()

    // Assign profile to tenant & persist across reload
    await page.getByRole('tab', { name: 'Çalışma alanı' }).click()
    await page.getByRole('button', { name: 'Profil ata' }).click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel('Bant genişliği profili').selectOption({ label: profName })
    await dialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog).toBeHidden()
    await expect(page.getByText(`Atanan profil: ${profName}`)).toBeVisible()
    await page.reload()
    await expect(page.getByText(`Atanan profil: ${profName}`)).toBeVisible()

    // Unassign profile
    await page.getByRole('button', { name: 'Profil ata' }).click()
    await page.getByRole('dialog').getByLabel('Bant genişliği profili').selectOption('')
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByText('Atanan profil: Yok')).toBeVisible()

    // Delete profile
    await page.getByRole('tab', { name: 'Profiller' }).click()
    await page.getByRole('button', { name: 'Sil' }).first().click()
    await expect(page.getByRole('heading', { name: profName })).toHaveCount(0)

    // Seed a protocol agent + backup job to test per-agent & per-job bandwidth forms & persistence
    const agentHost = uniq('bw-agent')
    const jobName = uniq('bw-job')
    const s5Like = { tenantId: t.tenantId, tenantName: t.tenantName, account: t.account, tenant: t.api, platform: await platformApi() }
    const agent = await enrollAgent(s5Like, agentHost)
    try {
      await createJob(s5Like, agent, jobName)

      await page.goto(`${settingsUrl}/bandwidth?tab=agents`)
      await page.getByRole('button', { name: `Düzenle — ${agentHost}` }).click()
      dialog = page.getByRole('dialog')
      await dialog.getByLabel('Sınırsız (sınır yok)').uncheck()
      await dialog.getByLabel('Hız sınırı').fill('20')
      await dialog.getByRole('button', { name: 'Kaydet' }).click()
      await expect(dialog).toBeHidden()
      await expect(page.getByRole('row').filter({ hasText: agentHost })).toContainText('20 Mbit/sn')
      await page.reload()
      await expect(page.getByRole('row').filter({ hasText: agentHost })).toContainText('20 Mbit/sn')

      await page.getByRole('tab', { name: 'Yedekleme işleri' }).click()
      await page.getByRole('button', { name: `Düzenle — ${jobName}` }).click()
      dialog = page.getByRole('dialog')
      await dialog.getByLabel('Sınırsız (sınır yok)').uncheck()
      await dialog.getByLabel('Hız sınırı').fill('15')
      await dialog.getByRole('button', { name: 'Kaydet' }).click()
      await expect(dialog).toBeHidden()
      await expect(page.getByRole('row').filter({ hasText: jobName })).toContainText('15 Mbit/sn')
      await page.reload()
      await expect(page.getByRole('row').filter({ hasText: jobName })).toContainText('15 Mbit/sn')
    } finally {
      await agent.ctx.dispose()
    }
    expect(problems).toEqual([])
  })

  test.fixme('editing a bandwidth profile with windows succeeds instead of throwing 500 (#286)', async ({ page }) => {
    await signInAs(page, t.account)
    const name = uniq('prof-edit')
    await page.goto(`${settingsUrl}/bandwidth?tab=profiles`)
    await page.getByRole('button', { name: 'Yeni profil' }).click()
    let dialog = page.getByRole('dialog')
    await dialog.getByLabel('Profil adı').fill(name)
    await dialog.getByRole('button', { name: /Pencereyi listeye ekle/i }).click()
    await dialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog).toBeHidden()

    await page.getByRole('button', { name: 'Düzenle' }).first().click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel('Profil adı').fill(`${name}-r`)
    await dialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog).toBeHidden()
    await expect(page.getByRole('heading', { name: `${name}-r` })).toBeVisible()
  })
})

test.describe('recovery key', () => {
  test('rejects text, private keys (paste & file) and 2048-bit keys; accepts 4096-bit key via paste and PEM file upload, persists and removes', async ({ page }) => {
    test.setTimeout(90_000)
    await signInAs(page, t.account)
    const problems = watch(page, [{ status: 400, path: /\/encryption\/recovery-key$/ }])

    await page.goto(`${settingsUrl}/recovery-key`)
    await page.getByRole('button', { name: 'Kurtarma anahtarı ekle' }).click()
    const pem = page.getByLabel('Genel anahtar (PEM)')
    const save = page.getByRole('button', { name: 'Anahtarı kaydet' })
    await expect(save).toBeDisabled()

    await pem.fill('not a key')
    await save.click()
    await expect(page.getByText('Yalnızca PEM biçiminde bir genel anahtar yükleyin veya yapıştırın.')).toBeVisible()

    await pem.fill('-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----')
    await save.click()
    await expect(page.getByText(/Özel anahtar kabul edilmez\./)).toBeVisible()
    await expect(pem).toHaveValue('')

    await page.getByLabel('Genel anahtar dosyası').setInputFiles({
      name: 'private.pem',
      mimeType: 'application/x-pem-file',
      buffer: Buffer.from('-----BEGIN RSA PRIVATE KEY-----\nsecret\n-----END RSA PRIVATE KEY-----', 'utf8'),
    })
    await expect(page.getByText(/Özel anahtar kabul edilmez\./)).toBeVisible()
    await expect(pem).toHaveValue('')

    await pem.fill(publicKeyPem(2048))
    await save.click()
    await expect(page.getByRole('alert').first()).toBeVisible()
    await expect(page.getByText('Kurtarma anahtarınız kaydedildi.')).toBeHidden()

    // Save valid 4096-bit key via paste
    await pem.fill(publicKeyPem(4096))
    await save.click()
    await expect(page.getByText('Kurtarma anahtarınız kaydedildi. Sonraki şifreli yedeklere eklenir.')).toBeVisible()
    await page.reload()
    await expect(page.getByText('Kurtarma anahtarınız eklenmiş.')).toBeVisible()

    // Replace key via .pem file upload & verify persistence
    await page.getByText('Kendi anahtarınızı ekleyin veya değiştirin').click()
    await page.getByLabel('Genel anahtar dosyası').setInputFiles({
      name: 'recovery.pem',
      mimeType: 'application/x-pem-file',
      buffer: Buffer.from(publicKeyPem(4096), 'utf8'),
    })
    await page.getByRole('button', { name: 'Anahtarı değiştir' }).click()
    await expect(page.getByText('Kurtarma anahtarınız kaydedildi. Sonraki şifreli yedeklere eklenir.')).toBeVisible()
    await page.reload()
    await expect(page.getByText('Kurtarma anahtarınız eklenmiş.')).toBeVisible()

    // Remove recovery key with typed workspace name confirmation
    await page.getByRole('button', { name: 'Anahtarı kaldır' }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog.getByRole('button', { name: 'Kaldır' })).toBeDisabled()
    await dialog.getByRole('textbox').fill(t.tenantName)
    await dialog.getByRole('button', { name: 'Kaldır' }).click()
    await expect(page.getByText('Kendi kurtarma anahtarınız yok.')).toBeVisible()
    await page.reload()
    await expect(page.getByText('Kendi kurtarma anahtarınız yok.')).toBeVisible()
    expect(problems).toEqual([])
  })
})
