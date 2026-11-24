import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { expect, test } from '@playwright/test'
import { Api, enableAgentEnrollment, ensureTenant, ensureTenantUser, ensureUnlimitedPlan } from '../../api'
import { state } from '../../state'

const run = promisify(execFile)
const suffix = `${Date.now()}`
const account = { login: `e2e.s4.${suffix}`, password: 'E2e-S4-Passw0rd!2026' }
let tenantId: string

test.beforeAll(async () => {
  const platform = await Api.signIn(state().accounts.platformAdmin)
  try {
    tenantId = await ensureTenant(platform, `e2e-s4-${suffix}`)
    const userId = await ensureTenantUser(platform, [tenantId], account)
    await platform.call('POST', `/api/platform/tenants/${tenantId}/memberships`, { userId }, [200, 201, 409])
    await ensureUnlimitedPlan(platform, tenantId, 'E2E Unlimited')
    await enableAgentEnrollment(platform, tenantId)
  } finally { await platform.dispose() }
})

test('signed-out login has no console errors (#239)', async ({ page }) => {
  test.fail(true, 'https://github.com/mtsarac/backup-platform/issues/239')
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.goto('/login')
  await expect(page.locator('input[type="password"]')).toBeVisible()
  expect(errors).toEqual([])
})

test('backup source wizard validates Windows file and database fields', async ({ page }) => {
  await page.goto('/login')
  await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(account.login)
  await page.locator('input[type="password"]').fill(account.password)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL(new RegExp(`/tenants/${tenantId}`))
  await page.goto(`/tenants/${tenantId}/backup-jobs/new`)
  await page.getByRole('button', { name: 'Elle yapılandır' }).click()
  await page.getByRole('radio', { name: 'Dosya / Klasör' }).check()
  await page.getByRole('button', { name: 'İleri' }).click()
  await expect(page.getByText('Geçerli bir dosya kaynağı referansı girin.')).toBeVisible()
  await page.getByRole('radio', { name: 'PostgreSQL' }).check()
  await page.getByRole('button', { name: 'İleri' }).click()
  await expect(page.getByText('Veritabanı sunucusunun ana bilgisayar adını girin.')).toBeVisible()
  await page.getByRole('radio', { name: 'Microsoft SQL Server' }).check()
  await page.getByRole('button', { name: 'İleri' }).click()
  await expect(page.getByText('Veritabanı adını girin.')).toBeVisible()
})

test('file backup wizard explains snapshot sources when VSS is enabled', async ({ page }) => {
  test.fixme(true, "windowsVss stack'te kapalı")
  await page.goto('/login')
  await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(account.login)
  await page.locator('input[type="password"]').fill(account.password)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL(new RegExp(`/tenants/${tenantId}`))
  await page.goto(`/tenants/${tenantId}/backup-jobs/new`)
  await page.getByRole('button', { name: 'Elle yapılandır' }).click()
  await page.getByRole('radio', { name: 'Dosya / Klasör' }).check()
  await expect(page.getByText(/Noktasal görünüm için yapılandırılmış anlık görüntü kaynağı kullanın/)).toBeVisible()
})

test('Windows enrollment command connects the service', async ({ page }) => {
  test.setTimeout(240_000)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/login')
  await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(account.login)
  await page.locator('input[type="password"]').fill(account.password)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL(new RegExp(`/tenants/${tenantId}`))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('requestfailed', request => { if (request.failure()?.errorText !== 'net::ERR_ABORTED') errors.push(`${request.method()} ${request.url()}: ${request.failure()?.errorText}`) })
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`) })
  await page.goto(`/tenants/${tenantId}/agents`)
  await page.getByRole('button', { name: /Yedekleme bilgisayarı ekle|Add a backup computer/ }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: /İleri|Next/ })).toBeDisabled()
  await dialog.getByLabel(/Bilgisayarın adı|Computer name/).fill(`e2e-s4-${suffix}`)
  await dialog.getByLabel(/İşletim sistemi|Operating system/).selectOption('Windows')
  await dialog.getByRole('button', { name: /İleri|Next/ }).click()
  await dialog.getByRole('button', { name: /Kayıt anahtarı oluştur|Create enrollment key/ }).click()
  const command = await dialog.getByLabel(/Tek kullanımlık kurulum komutu|One-time installation command/).inputValue()
  expect(command).toContain('/api/agent-dist/install.ps1')
  const encoded = Buffer.from(command, 'utf16le').toString('base64')
  try {
    await run('ssh', ['-o', 'BatchMode=yes', 'virtual-win11', 'powershell', '-NoProfile', '-EncodedCommand', encoded], { timeout: 180_000, maxBuffer: 2_000_000 })
  } catch {
    throw new Error('Windows installation failed; inspect the Windows agent service and installer logs without exposing the enrollment token')
  }
  await expect(dialog.getByRole('status')).toContainText(/Çevrimiçi|Online/, { timeout: 90_000 })
  await expect(dialog.getByLabel(/Tek kullanımlık kurulum komutu|One-time installation command/)).toHaveCount(0)
  const service = await run('ssh', ['-o', 'BatchMode=yes', 'virtual-win11', 'powershell', '-NoProfile', '-Command', '(Get-Service BackupAgent).Status'])
  await dialog.getByRole('button', { name: /Tamam|Done/ }).click()
  const agent = page.getByRole('link', { name: `e2e-s4-${suffix}` })
  await expect(agent).toBeVisible()
  const agentUrl = await agent.getAttribute('href')
  await agent.click()
  await expect(page).toHaveURL(new RegExp(`${agentUrl}$`))
  await page.reload()
  await expect(page).toHaveURL(new RegExp(`${agentUrl}$`))
  await page.goBack()
  await expect(page.getByRole('link', { name: `e2e-s4-${suffix}` })).toBeVisible()
  await page.goForward()
  await expect(page).toHaveURL(new RegExp(`${agentUrl}$`))
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByRole('main')).toBeVisible()
  await page.setViewportSize({ width: 1440, height: 900 })
  await expect(page.getByRole('main')).toBeVisible()
  expect(service.stdout.trim()).toBe('Running')
  expect(errors).toEqual([])
})
