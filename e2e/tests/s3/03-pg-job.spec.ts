import { expect, test, type Page } from '@playwright/test'
import { setTimeout as sleep } from 'node:timers/promises'
import { type Api } from '../../api'
import { ensureAgent, seedS3, vm } from './fixture'
import { PG, databaseFingerprint, seedSourceDatabase } from './pg'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
const suffix = Date.now().toString(36)
let sourceFingerprint: string
const sourceDb = `e2e_s3_src_${suffix}`
const firstDb = `e2e_s3_first_${suffix}`
const jobName = `e2e-s3-pg-${suffix}`
const restoredDb = `e2e_s3_restored_${suffix}`

test.describe.configure({ mode: 'serial', timeout: 180_000 })

test.beforeAll(async () => {
  test.setTimeout(180_000)
  ;({ tenantId, tenant } = await seedS3())
  const agentId = await ensureAgent(tenant, tenantId)
  seedSourceDatabase(sourceDb)
  seedSourceDatabase(firstDb, 50)
  sourceFingerprint = databaseFingerprint(sourceDb)
  // Ensure the discovered PostgreSQL instance has a working login and has scanned the newly created databases
  // (retries past the 5 s rescan_too_soon throttle when running immediately after 02-discovery).
  const base = `/api/tenants/${tenantId}/discovery`
  let pgId = ''
  const deadline = Date.now() + 90_000
  while (Date.now() < deadline) {
    if (!pgId) {
      await tenant.call('POST', `${base}/agents/${agentId}/rescan`, undefined, [200, 202, 429])
      const list = await tenant.call('GET', `${base}/instances?agentId=${agentId}`)
      const pg = list.items.find((x: any) => x.engine === 'postgres')
      if (pg) {
        pgId = pg.id
        await tenant.call('PUT', `${base}/instances/${pgId}/credential`, {
          credentialReference: null,
          databaseLogin: { userName: PG.user, password: PG.password },
          integratedSecurity: false,
          encrypt: false,
          trustServerCertificate: true,
        })
      }
    }
    if (pgId) {
      await tenant.call('POST', `${base}/instances/${pgId}/verify`, undefined, [200, 202, 429])
      const detail = await tenant.call('GET', `${base}/instances/${pgId}`)
      const names = new Set((detail.databases ?? []).map((d: any) => d.name))
      if (detail.access === 'Authenticated' && names.has(sourceDb) && names.has(firstDb)) break
    }
    await sleep(2500)
  }
})

test.afterAll(async () => {
  const jobs: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/backup-jobs`).catch(() => [])
  for (const j of jobs.filter(x => x.name === jobName || x.configuration?.database === firstDb)) {
    await tenant.call('DELETE', `/api/tenants/${tenantId}/backup-jobs/${j.id}`, undefined, [200, 204]).catch(() => {})
  }
  await tenant.dispose()
  vm(`sudo -n -u postgres psql -q -c "drop database if exists ${restoredDb}" -c "drop database if exists ${sourceDb}" -c "drop database if exists ${firstDb}"`)
})

test('job wizard creates a PostgreSQL job from discovery', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'agents/databases'))
  await page.getByRole('link', { name: /PostgreSQL/ }).first().click()
  await expect(page.getByRole('checkbox', { name: `${sourceDb} seç` })).toBeEnabled({ timeout: 30_000 })
  await page.getByRole('checkbox', { name: `${sourceDb} seç` }).check()
  await page.getByRole('button', { name: 'Yedekleme işi oluştur' }).click()
  await page.getByLabel('İş adı').fill(jobName)
  for (let i = 0; i < 3; i++) await page.getByRole('button', { name: 'İleri' }).click()
  await expect(page.getByText('Her şey doğru mu?')).toBeVisible()
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page).toHaveURL(/\/backup-jobs$/)
  await expect(page.getByRole('row', { name: new RegExp(jobName) })).toContainText('İlk yedek bekleniyor')
  w.expectClean()
})

test('Run now backs up the database and the run succeeds', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'backup-jobs'))
  await page.getByRole('link', { name: jobName }).click()
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await expect(page.getByText('Yedekleme denemesi başlatıldı')).toBeVisible()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  await expect(page).toHaveURL(/backup-runs\/[0-9a-f-]+$/)
  await expect(page.getByText('Çoğaltma sağlıklı')).toBeVisible({ timeout: 90_000 })
  await page.goto(tenantPath(tenantId, 'backup-runs'))
  await expect(page.getByRole('row', { name: new RegExp(jobName) })).toContainText('Başarılı')
  w.expectClean()
})

test.fixme('small PostgreSQL backup artifact displays non-zero size instead of 0 MB (#265)', async ({ page }) => {
  await signIn(page)
  await page.goto(tenantPath(tenantId, 'backup-runs'))
  const row = page.getByRole('row', { name: new RegExp(jobName) })
  await expect(row).not.toContainText(/\b0 MB\b/)
})

async function startRestore(page: Page, database: string) {
  await page.goto(tenantPath(tenantId, 'backup-runs'))
  await page.getByRole('link', { name: jobName }).first().click()
  await page.getByRole('link', { name: 'Geri yükle', exact: true }).click()
  await page.getByLabel('Yeni Veritabanı Adı').fill(database)
  await page.getByRole('button', { name: 'Devam et →' }).click()
}

test('Restore precheck fails for a login without the superuser privilege', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await startRestore(page, restoredDb)
  await expect(page.getByText('Ön kontrol başarısız oldu! Geri yükleme başlatılamaz.')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByRole('row', { name: /PostgreSQL geri yükleme ön kontrolü/ })).toContainText('Başarısız')
  await expect(page.getByRole('button', { name: 'Geri yüklemeyi başlat' })).toHaveCount(0)
  w.expectClean()
})

test.fixme('Restore precheck names the missing superuser privilege in plain language (#263)', async ({ page }) => {
  await signIn(page)
  await startRestore(page, restoredDb)
  await expect(page.getByRole('row', { name: /PostgreSQL geri yükleme ön kontrolü/ })).toContainText(/süper kullanıcı|yönetici|superuser/i, { timeout: 60_000 })
})

test('Restore wizard restores into a new database with identical data', async ({ page }) => {
  vm(`sudo -n -u postgres psql -q -c "alter role ${PG.user} superuser"`)
  const w = watch(page)
  await signIn(page); w.reset()
  await startRestore(page, restoredDb)
  await page.getByRole('button', { name: 'Geri yüklemeyi başlat' }).click({ timeout: 60_000 })
  await expect(page).toHaveURL(/\/restores\/[0-9a-f-]+$/)
  await expect(page.getByText('Tamamlandı · %100')).toBeVisible({ timeout: 90_000 })
  await expect(page.getByText('Yedekleme bilgisayarı, geri yüklenen veritabanının çalışır durumda olduğunu bildirdi.')).toBeVisible()
  expect(databaseFingerprint(restoredDb)).toBe(sourceFingerprint)
  w.expectClean()
})

test('Restore wizard replaces an existing database and keeps the bp_old_* rollback copy until retention ends', async ({ page }) => {
  vm(`PGPASSWORD='${PG.password}' psql -q -h 127.0.0.1 -U ${PG.user} ${restoredDb} -c "delete from customers where id > 10"`)
  expect(databaseFingerprint(restoredDb)).not.toBe(sourceFingerprint)

  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'backup-runs'))
  await page.getByRole('link', { name: jobName }).first().click()
  await page.getByRole('link', { name: 'Geri yükle', exact: true }).click()

  await page.getByRole('button', { name: /Yerine geç/ }).click()
  await expect(page.getByText('Veri kaybı riski')).toBeVisible()

  // Wait for target discovery select to list restoredDb, then pick it
  const dbField = page.getByLabel('Yerine geçilecek veritabanı')
  await expect(async () => {
    const tag = await dbField.evaluate(el => el.tagName.toLowerCase())
    if (tag === 'select') await dbField.selectOption(restoredDb)
    else await dbField.fill(restoredDb)
  }).toPass({ timeout: 30_000 })

  await page.getByRole('button', { name: 'Devam et →' }).click()
  await page.getByRole('button', { name: 'Geri yüklemeyi başlat' }).click({ timeout: 60_000 })

  const replaceDialog = page.getByRole('dialog')
  await expect(replaceDialog).toContainText('Mevcut veritabanı değiştirilsin mi?')
  const confirmBtn = replaceDialog.getByRole('button', { name: 'Üzerine yaz ve geri yükle' })
  await expect(confirmBtn).toBeDisabled()
  await replaceDialog.getByRole('textbox').fill(restoredDb)
  await expect(confirmBtn).toBeEnabled()
  await confirmBtn.click()

  await expect(page).toHaveURL(/\/restores\/[0-9a-f-]+$/)
  await expect(page.getByText('Tamamlandı · %100')).toBeVisible({ timeout: 90_000 })
  expect(databaseFingerprint(restoredDb)).toBe(sourceFingerprint)

  // Rollback copy (bp_old_*) is retained for the protection period
  const rollbackNote = page.getByText('Geri alma kopyası saklanıyor:')
  await expect(rollbackNote).toBeVisible()
  const rollbackDb = (await rollbackNote.locator('code').textContent())!.trim()
  expect(rollbackDb).toMatch(/^bp_old_/)
  expect(vm(`sudo -n -u postgres psql -qtA -c "select 1 from pg_database where datname='${rollbackDb}'"`).trim()).toBe('1')

  // Removal is refused until retention ends: the UI shows the protection deadline and offers no remove button
  await expect(page.getByText(/Koruma süresi sonu:/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Geri alma kopyasını kaldır' })).toHaveCount(0)
  w.expectClean()
})

test('Isolated restore verification proves the PostgreSQL backup is restorable', async ({ page }) => {
  vm(`sudo -n -u postgres psql -q -c "alter role ${PG.user} superuser"`)
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'backup-runs'))
  await page.getByRole('link', { name: jobName }).first().click()
  await page.getByRole('button', { name: 'Geri yükleme testi başlat' }).click()
  await expect(page).toHaveURL(/\/restores\/[0-9a-f-]+$/)
  await expect(page.getByText('Tamamlandı · %100')).toBeVisible({ timeout: 90_000 })
  await expect(page.getByText('Başarılı').first()).toBeVisible()
  w.expectClean()
})

test('First-backup guide (/first-backup) creates and runs a PostgreSQL backup from discovered data', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'first-backup'))
  await expect(page.getByRole('heading', { name: 'İlk yedeğinizi alın' })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: new RegExp(firstDb) })).toBeVisible({ timeout: 60_000 })
  await page.getByRole('checkbox', { name: new RegExp(firstDb) }).check()
  await page.getByRole('button', { name: 'İleri' }).click()
  await expect(page.getByRole('heading', { name: 'Özet ve başla' })).toBeVisible()
  await page.getByRole('button', { name: 'Yedeklemeyi oluştur ve ilk yedeği başlat' }).click()
  await expect(page.getByText('İlk yedeğiniz alındı ve doğrulandı')).toBeVisible({ timeout: 90_000 })
  w.expectClean([/rescan/])
})
