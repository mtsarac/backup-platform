import { expect, test } from '@playwright/test'
import { activeAgent, createFixtureDatabase, dbCredential, discoverConfigureAndRun, dropDatabase, signIn, sql, watchErrors } from './support'

const engines = ['postgresql', 'mssql'] as const
const checksum = (engine: (typeof engines)[number]) => engine === 'postgresql'
  ? "SELECT count(*)||'|'||string_agg(value, ',' ORDER BY id) FROM s4_items"
  : "SELECT CONCAT(COUNT(*),'|',STRING_AGG(CAST(value AS nvarchar(max)),',') WITHIN GROUP (ORDER BY id)) FROM s4_items"

for (const engine of engines) {
  test(`Windows ${engine} discovery, credential error/happy path, backup and restore through the UI`, async ({ page }) => {
    test.setTimeout(360_000)
    const [active, credential] = await Promise.all([activeAgent(), dbCredential(engine)])
    const database = `e2es4_${engine}_${Date.now()}`
    const restored = `${database}_r`
    await createFixtureDatabase(engine, credential, database)
    try {
      await signIn(page, active)
      const errors = watchErrors(page)
      const name = `e2e-s4-${database}`
      await discoverConfigureAndRun(page, active, engine, credential, database, name)
      await page.goto(`/tenants/${active.tenantId}/backup-runs`)
      await expect(page.getByRole('link', { name })).toBeVisible()
      await page.getByRole('link', { name }).click()
      await page.getByRole('link', { name: 'Geri yükle', exact: true }).click()
      await page.getByLabel('Yeni Veritabanı Adı').fill(restored)
      await page.getByRole('button', { name: 'Devam et →' }).click()
      await page.getByRole('button', { name: 'Geri yüklemeyi başlat' }).click({ timeout: 120_000 })
      await expect(page).toHaveURL(/\/restores\//)
      await expect(page.getByText('Başarılı', { exact: true }).first()).toBeVisible({ timeout: 180_000 })
      expect(await sql(engine, credential, restored, checksum(engine))).toBe(await sql(engine, credential, database, checksum(engine)))
      expect(errors).toEqual([])
    } finally {
      await dropDatabase(engine, credential, restored).catch(() => undefined)
      await dropDatabase(engine, credential, database).catch(() => undefined)
    }
  })

  test(`manual ${engine} job form accepts a database login`, async ({ page }) => {
    test.setTimeout(240_000)
    const [active, credential] = await Promise.all([activeAgent(), dbCredential(engine)])
    const database = `e2es4_manual_${engine}_${Date.now()}`
    await createFixtureDatabase(engine, credential, database)
    try {
      await signIn(page, active)
      await page.goto(`/tenants/${active.tenantId}/backup-jobs/new`)
      await page.getByRole('button', { name: 'Elle yapılandır' }).click()
      const name = `e2e-s4-${database}`
      await page.getByLabel('İş adı').fill(name)
      await page.getByLabel('Yedekleme bilgisayarı', { exact: true }).selectOption(active.agentId)
      await page.getByRole('radio', { name: engine === 'postgresql' ? 'PostgreSQL' : 'Microsoft SQL Server' }).check()
      await page.getByRole('textbox', { name: engine === 'postgresql' ? 'PostgreSQL ana bilgisayarı' : /SQL Server ana bilgisayarı/ }).fill('127.0.0.1')
      await page.getByLabel('Veritabanı', { exact: true }).fill(database)
      if (engine === 'mssql') await page.getByRole('radio', { name: 'SQL Server oturumu (kullanıcı adı ve parola)' }).check()
      await page.getByRole('checkbox', { name: 'Bağlantıyı şifrele' }).uncheck()
      await page.getByLabel('Veritabanı kullanıcı adı').fill(credential.user, { timeout: 5_000 })
      await page.getByLabel('Veritabanı parolası').fill(credential.password)
      await page.getByRole('button', { name: 'İleri' }).click()
      await page.getByRole('button', { name: 'İleri' }).click()
      await page.getByRole('button', { name: 'Elle çalıştır' }).click()
      await page.getByRole('button', { name: 'İleri' }).click()
      await page.getByRole('button', { name: 'Kaydet', exact: true }).click()
      await page.getByRole('link', { name, exact: true }).click()
      await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
      await page.getByRole('link', { name: 'Denemeyi aç' }).click()
      await expect(page.getByRole('link', { name: 'Geri yükle', exact: true })).toBeVisible({ timeout: 120_000 })
    } finally { await dropDatabase(engine, credential, database).catch(() => undefined) }
  })
}
