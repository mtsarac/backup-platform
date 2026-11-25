import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { Api, createEnrollmentToken, enableAgentEnrollment, ensureTenant, ensureTenantUser, ensureUnlimitedPlan } from '../../api'
import { state } from '../../state'
import { account, ensureAgent, seedJob, seedS3, triggerAndWaitRun, vm } from './fixture'
import { FILE_SOURCE, seedFileSource } from './files'
import { seedSourceDatabase } from './pg'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
let agentId: string
let instanceId = ''
let jobId = ''
let runId = ''
let restoreId = ''
const suffix = Date.now().toString(36)
const jobName = `e2e-s3-screen-${suffix}`
const screenDb = `e2e_s3_screen_${suffix}`

test.describe.configure({ mode: 'serial', timeout: 300_000 })

test.beforeAll(async () => {
  test.setTimeout(180_000)
  ;({ tenantId, tenant } = await seedS3())
  agentId = await ensureAgent(tenant, tenantId)
  await seedFileSource(tenant, tenantId)
  seedSourceDatabase(screenDb, 10)

  const instances = await tenant.call('GET', `/api/tenants/${tenantId}/discovery/instances?agentId=${agentId}`)
  instanceId = instances.items.find((i: any) => i.engine === 'postgres')?.id ?? ''

  jobId = await seedJob(tenant, tenantId, {
    name: jobName,
    agentId,
    overlapPolicy: 'Skip',
    configuration: {
      engine: 'file-system', database: FILE_SOURCE, host: 'localhost', port: null, integratedSecurity: true,
      databaseCredentialReference: null, encrypt: false, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
      files: { paths: ['docs'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
      destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
    },
  })
  const seeded = await triggerAndWaitRun(tenant, tenantId, jobId)
  runId = seeded.runId

  const pendingTokens: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/agent-enrollment-tokens`)
  for (const t of pendingTokens) {
    await tenant.call('DELETE', `/api/tenants/${tenantId}/agent-enrollment-tokens/${t.tokenId}`, undefined, [200, 204]).catch(() => {})
  }

  const restores = await tenant.call('GET', `/api/tenants/${tenantId}/restores?pageSize=1`)
  restoreId = restores.items?.[0]?.id ?? ''
  if (!restoreId) {
    const r = await tenant.call('POST', `/api/tenants/${tenantId}/restores`, {
      artifactId: seeded.artifactId,
      agentId,
      mode: 'CreateNewDatabase',
      target: { host: 'localhost', integratedSecurity: true, encrypt: false, trustServerCertificate: false },
      options: { verifyChecksum: true, verifyBackup: true, files: { relativeDirectory: `restored-screen-${suffix}` } },
    }, [200, 201, 202], { 'Idempotency-Key': `s3-screen-restore-${suffix}` })
    restoreId = r.id
  }
})

test.afterAll(async () => {
  if (jobId) await tenant.call('DELETE', `/api/tenants/${tenantId}/backup-jobs/${jobId}`, undefined, [200, 204]).catch(() => {})
  vm(`sudo -n -u postgres psql -q -c "drop database if exists ${screenDb}"`)
  await tenant.dispose()
})

function populatedRoutes() {
  const routes = [
    tenantPath(tenantId, 'agents'),
    tenantPath(tenantId, `agents/${agentId}`),
    tenantPath(tenantId, 'agents/databases'),
    ...(instanceId ? [tenantPath(tenantId, `discovery/instances/${instanceId}`)] : []),
    tenantPath(tenantId, 'enrollment'),
    tenantPath(tenantId, 'first-backup'),
    tenantPath(tenantId, 'backup-jobs'),
    tenantPath(tenantId, 'backup-jobs/new'),
    tenantPath(tenantId, `backup-jobs/${jobId}`),
    tenantPath(tenantId, 'backup-runs'),
    tenantPath(tenantId, `backup-runs/${runId}`),
    tenantPath(tenantId, `backup-runs/${runId}/restore`),
    tenantPath(tenantId, 'restores'),
    ...(restoreId ? [tenantPath(tenantId, `restores/${restoreId}`)] : []),
    tenantPath(tenantId, 'backup-shares'),
  ]
  return routes
}

async function inspectScreen(page: Page, path: string, w: ReturnType<typeof watch>) {
  w.reset()
  await page.goto(path)
  await expect.soft(page.getByRole('main'), `${path} main`).toBeVisible()
  await expect.soft(page.getByRole('heading').first(), `${path} heading`).toBeVisible()
  await page.waitForTimeout(400)
  const axe = await new AxeBuilder({ page }).analyze()
  const serious = axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect.soft(serious.map(v => `${v.id}: ${v.nodes[0]?.target}`), `${path} axe`).toEqual([])
  expect.soft(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), `${path} no horizontal overflow`).toBe(true)
  await expect.soft(page.getByText(/undefined|NaN|\[object Object\]/)).toHaveCount(0)
  await page.reload()
  await expect.soft(page.getByRole('main'), `${path} reload`).toBeVisible()
  const problems = w.problems.filter(p => !/rescan/.test(p))
  expect.soft(problems, `${path} console/network`).toEqual([])
}

for (const vp of [{ name: 'desktop', width: 1440, height: 900 }, { name: 'mobile', width: 390, height: 844 }]) {
  test(`S3 screens pass axe, overflow, reload, and console/network cleanliness on ${vp.name}`, async ({ page }) => {
    await page.setViewportSize({ width: vp.width, height: vp.height })
    const w = watch(page)
    await signIn(page)
    for (const route of populatedRoutes()) await inspectScreen(page, route, w)
  })
}

test('enrollment page with a pending token passes WCAG 2.5.3 label-content-name-mismatch on Revoke button (#268)', async ({ page }) => {
  await createEnrollmentToken(tenant, tenantId)
  try {
    await signIn(page)
    await page.goto(tenantPath(tenantId, 'enrollment'))
    await expect(page.getByRole('button', { name: /^İptal et/ }).first()).toBeVisible()
    const axe = await new AxeBuilder({ page }).analyze()
    expect(axe.violations.filter(v => v.id === 'label-content-name-mismatch')).toEqual([])
  } finally {
    const tokens: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/agent-enrollment-tokens`)
    for (const t of tokens) await tenant.call('DELETE', `/api/tenants/${tenantId}/agent-enrollment-tokens/${t.tokenId}`, undefined, [200, 204]).catch(() => {})
  }
})

test('keyboard skip link, dialog focus + Escape, and browser back/forward navigation work cleanly', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'agents'))
  await expect(page.getByRole('heading', { name: 'Bilgisayarlar' })).toBeVisible()
  await page.keyboard.press('Tab')
  const skip = page.getByRole('link', { name: 'Ana içeriğe geç' })
  await expect(skip).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/#main$/)

  await page.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByLabel('Bilgisayarın adı')).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)

  await page.goto(tenantPath(tenantId, 'backup-jobs'))
  await page.getByRole('link', { name: jobName }).click()
  await expect(page).toHaveURL(new RegExp(`/backup-jobs/${jobId}$`))
  await page.goBack()
  await expect(page).toHaveURL(/\/backup-jobs$/)
  await page.goForward()
  await expect(page).toHaveURL(new RegExp(`/backup-jobs/${jobId}$`))
  w.expectClean()
})

test('wizard and form validations explain missing or invalid fields clearly', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'backup-jobs/new'))
  await page.getByRole('button', { name: 'Elle yapılandır' }).click()
  await page.getByRole('radio', { name: 'Dosya / Klasör' }).check()
  await page.getByRole('button', { name: 'İleri' }).click()
  await expect(page.getByText('Geçerli bir dosya kaynağı referansı girin.')).toBeVisible()
  await page.getByRole('radio', { name: 'PostgreSQL' }).check()
  await page.getByRole('button', { name: 'İleri' }).click()
  await expect(page.getByText('Veritabanı sunucusunun ana bilgisayar adını girin.')).toBeVisible()

  if (instanceId) {
    await page.goto(tenantPath(tenantId, `discovery/instances/${instanceId}`))
    const change = page.getByText('Giriş bilgilerini değiştir', { exact: true })
    if (await change.isVisible()) await change.click()
    await page.getByLabel('Veritabanı kullanıcı adı').fill('')
    await page.getByLabel('Veritabanı parolası').fill('')
    await page.getByRole('button', { name: 'Kaydet ve doğrula' }).click()
    await expect(page.getByText(/kullanıcı adı|parola/i).first()).toBeVisible()
  }
  w.expectClean()
})

test('empty tenant states, unknown UUID not-found states, and simulated 500 API recovery', async ({ page }) => {
  const platform = await Api.signIn(state().accounts.platformAdmin)
  const emptyAcc = { login: `e2e.s3.empty.${suffix}`, password: account.password }
  const emptyTenantId = await ensureTenant(platform, `e2e-s3-empty-${suffix}`)
  const userId = await ensureTenantUser(platform, [emptyTenantId], emptyAcc)
  await platform.call('POST', `/api/platform/tenants/${emptyTenantId}/memberships`, { userId }, [200, 201, 409])
  await ensureUnlimitedPlan(platform, emptyTenantId, 'E2E Unlimited')
  await enableAgentEnrollment(platform, emptyTenantId)
  await platform.dispose()

  const w = watch(page)
  await signIn(page, emptyAcc.login, emptyAcc.password); w.reset()

  const emptyChecks: [string, RegExp][] = [
    ['agents', /Henüz yedekleme bilgisayarı eklenmedi/],
    ['agents/databases', /Henüz yedekleme bilgisayarı yok/],
    ['enrollment', /Bekleyen kayıt anahtarı yok/],
    ['first-backup', /İlk yedeğinizi alın/],
    ['backup-jobs', /Henüz yedekleme işi oluşturulmadı/],
    ['backup-runs', /Henüz yedekleme denemesi yok/],
    ['restores', /Henüz geri yükleme yapılmadı/],
    ['backup-shares', /Henüz paylaşım yok/],
  ]
  for (const [sub, text] of emptyChecks) {
    await page.goto(tenantPath(emptyTenantId, sub))
    await expect(page.getByText(text).first(), sub).toBeVisible()
  }
  w.expectClean()

  // Unknown IDs show clean not-found states (404 from API is expected on detail lookups)
  const zero = '11111111-2222-4333-8444-555555555555'
  for (const [sub, text] of [
    [`agents/${zero}`, /bulunamadı/i],
    [`backup-jobs/${zero}`, /Yedekleme işi bulunamadı/],
    [`backup-runs/${zero}`, /bulunamadı/i],
    [`restores/${zero}`, /bulunamadı/i],
  ] as const) {
    await page.goto(tenantPath(emptyTenantId, sub))
    await expect(page.getByText(text).first(), sub).toBeVisible()
  }

  // Simulated 500 error on /restores shows alert and recovers
  await page.route('**/api/tenants/*/restores**', r => r.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"boom"}' }))
  await page.goto(tenantPath(emptyTenantId, 'restores'))
  await expect(page.getByRole('alert').first()).toBeVisible()
  await page.unroute('**/api/tenants/*/restores**')
  await page.reload()
  await expect(page.getByText(/Henüz geri yükleme yapılmadı/)).toBeVisible()
})

test('role limits, tenant isolation, unauthenticated redirects, and session expiry', async ({ page, context }) => {
  for (const sub of ['agents', 'enrollment', 'first-backup', 'backup-jobs', 'backup-runs', 'restores']) {
    await page.goto(tenantPath(tenantId, sub))
    await expect(page).toHaveURL(/\/login/)
  }

  await signIn(page)
  await page.goto('/platform/agents')
  await expect(page.getByText(/yalnızca platform yöneticileri|PlatformAdmin/i).first()).toBeVisible()

  await page.goto('/tenants/00000000-0000-0000-0000-000000000099/agents')
  await expect(page.getByText(/bulunamadı|not found/i).first()).toBeVisible()

  await context.clearCookies()
  await page.goto(tenantPath(tenantId, 'backup-jobs'))
  await expect(page).toHaveURL(/\/login/)

  await signIn(page)
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)
  await page.goto(tenantPath(tenantId, 'agents'))
  await expect(page).toHaveURL(/\/login/)
})

test('TR / EN language switcher translates headings and tabs consistently across S3 screens', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'agents'))
  await expect(page.getByRole('heading', { name: 'Bilgisayarlar' })).toBeVisible()

  await page.getByRole('button', { name: /Dili değiştir|Change language/i }).click()
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')
  await expect(page.getByRole('heading', { name: 'Computers' })).toBeVisible()

  for (const [sub, enHeading] of [
    ['enrollment', /New computer|Enrollment/i],
    ['first-backup', /first backup/i],
    ['backup-jobs', /Backups/i],
    ['backup-runs', /Backups/i],
    ['restores', /Backups/i],
  ] as const) {
    await page.goto(tenantPath(tenantId, sub))
    await expect(page.getByRole('heading', { name: enHeading }).first(), sub).toBeVisible()
    await expect(page.getByText(/undefined|NaN|\[object Object\]/)).toHaveCount(0)
  }

  await page.getByRole('button', { name: /Dili değiştir|Change language/i }).click()
  await expect(page.locator('html')).toHaveAttribute('lang', 'tr')
  await expect(page.getByRole('heading', { name: 'Yedeklemeler' })).toBeVisible()
  w.expectClean([/rescan/])
})

test('empty GUID detail URLs show the same not-found state as any unknown id instead of 400 "Girilen değer geçersiz" (#274)', async ({ page }) => {
  await signIn(page)
  for (const sub of ['agents', 'restores', 'backup-runs']) {
    await page.goto(tenantPath(tenantId, `${sub}/00000000-0000-0000-0000-000000000000`))
    await expect(page.getByText(/bulunamadı/i).first(), sub).toBeVisible()
  }
})
