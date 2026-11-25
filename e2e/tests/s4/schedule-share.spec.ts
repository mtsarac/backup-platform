import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { expect, test } from '@playwright/test'
import { Api } from '../../api'
import { state } from '../../state'
import { activeAgent, password, seedFileJob, signIn, watchErrors, writeSourceFile } from './support'

test('schedule can be configured in the UI and a scheduled Windows file job executes on time', async ({ page }) => {
  test.setTimeout(360_000)
  const active = await activeAgent()
  const stamp = Date.now()
  const file = `s4-schedule-${stamp}.txt`
  const uiJobName = `e2e-s4-sched-ui-${stamp}`
  const cronJobName = `e2e-s4-schedule-${stamp}`
  await writeSourceFile(file, 'scheduled fixture')
  const uiJobId = await seedFileJob(active, uiJobName, file)
  // The create/edit wizard offers daily presets only, so a 1-minute cron is seeded to verify live scheduled execution.
  const cronJobId = await seedFileJob(active, cronJobName, file, '* * * * *')
  try {
    await signIn(page, active)
    const errors = watchErrors(page)

    await page.goto(`/tenants/${active.tenantId}/backup-jobs/${uiJobId}`)
    await page.getByRole('button', { name: 'Değiştir: Zamanlama' }).click()
    await page.getByLabel('Hazır zamanlamalar').selectOption('0 2 * * *')
    await page.getByRole('button', { name: 'Kaydet', exact: true }).click()
    await expect(page.getByText('Her gün 02:00')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Zamanlamayı durdur' })).toBeVisible()
    await page.getByRole('button', { name: 'Zamanlamayı durdur' }).click()
    await expect(page.getByRole('button', { name: 'Zamanlamayı sürdür' })).toBeVisible()

    await page.goto(`/tenants/${active.tenantId}/backup-jobs/${cronJobId}`)
    await expect(page.getByText('Özel zamanlama')).toBeVisible()
    await expect(async () => {
      await page.goto(`/tenants/${active.tenantId}/backup-runs`)
      await expect(page.getByRole('row').filter({ hasText: cronJobName }).first().getByRole('cell', { name: 'Başarılı' })).toBeVisible({ timeout: 2_000 })
    }).toPass({ timeout: 240_000, intervals: [5_000] })

    await page.goto(`/tenants/${active.tenantId}/backup-jobs/${cronJobId}`)
    await page.getByRole('button', { name: 'Zamanlamayı durdur' }).click()
    await expect(page.getByRole('button', { name: 'Zamanlamayı sürdür' })).toBeVisible()
    expect(errors).toEqual([])
  } finally {
    const api = await Api.signIn({ login: active.login, password })
    await api.call('POST', `/api/tenants/${active.tenantId}/backup-jobs/${cronJobId}/disable`, undefined, [200, 204, 404]).catch(() => undefined)
    await api.call('POST', `/api/tenants/${active.tenantId}/backup-jobs/${uiJobId}/disable`, undefined, [200, 204, 404]).catch(() => undefined)
    await api.dispose()
  }
})

test('share lifecycle works end-to-end and the direct download matches SHA-256 where WebRTC is reachable', async ({ page, browser }) => {
  test.setTimeout(360_000)
  await page.addInitScript(() => {
    const store: Record<string, number[]> = {}
    ;(window as unknown as { __savedDownloads: Record<string, number[]> }).__savedDownloads = store
    ;(window as unknown as { showSaveFilePicker: unknown }).showSaveFilePicker = async ({ suggestedName }: { suggestedName: string }) => ({
      async createWritable() {
        const chunks: number[] = []
        return {
          async write(chunk: Uint8Array | ArrayBuffer) {
            const bytes = chunk instanceof Uint8Array ? chunk : new Uint8Array(chunk)
            for (const b of bytes) chunks.push(b)
          },
          async close() { store[suggestedName] = chunks },
          async abort() { chunks.length = 0 },
        }
      },
    })
  })

  const active = await activeAgent()
  const stamp = Date.now()
  const file = `s4-share-${stamp}.txt`
  const name = `e2e-s4-share-${stamp}`
  const reason = `e2e s4 share ${stamp}`
  await writeSourceFile(file, `share fixture ${stamp}`)
  const jobId = await seedFileJob(active, name, file)
  await signIn(page, active)
  const errors = watchErrors(page)

  await page.goto(`/tenants/${active.tenantId}/backup-jobs/${jobId}`)
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  await expect(page.getByRole('link', { name: 'Geri yükle', exact: true })).toBeVisible({ timeout: 120_000 })
  await page.getByText('Diğer', { exact: true }).first().click()

  // The direct channel is WebRTC between this browser and the agent; the libvirt firewall drops VM-to-host ICE traffic in this lab (#277), so it runs only where that path is open (S4_DIRECT_DOWNLOAD=1).
  if (process.env.S4_DIRECT_DOWNLOAD === '1') {
    await page.getByRole('tabpanel', { name: 'Özet' }).getByRole('button', { name: 'İndir', exact: true }).click()
    const downloadRegion = page.getByRole('region', { name: /Yedeklemeyi indir/i })
    const checksum = (await downloadRegion.locator('code').first().textContent())?.trim()
    expect(checksum).toMatch(/^[0-9a-f]{64}$/)
    await downloadRegion.getByRole('button', { name: /Yedeklemeyi indir/i }).click()
    const saveButton = downloadRegion.getByRole('button', { name: 'Dosyayı kaydet' })
    await expect(saveButton).toBeVisible({ timeout: 30_000 })
    await saveButton.click()
    await expect(downloadRegion.getByRole('button', { name: 'Doğrulandı ve diske kaydedildi.' })).toBeVisible({ timeout: 90_000 })
    const savedBytes = await page.evaluate(() => {
      const entries = Object.values((window as unknown as { __savedDownloads: Record<string, number[]> }).__savedDownloads)
      return entries[0] ?? []
    })
    expect(savedBytes.length).toBeGreaterThan(0)
    expect(createHash('sha256').update(Buffer.from(savedBytes)).digest('hex')).toBe(checksum)
  }

  await page.getByRole('button', { name: 'Paylaş', exact: true }).click()
  await page.getByRole('button', { name: 'Paylaşım oluştur' }).click()
  await page.getByPlaceholder('Paylaşım gerekçesini yazın').fill(reason)
  await page.getByRole('dialog').getByRole('button', { name: 'Paylaşım oluştur' }).click()
  await expect(page.getByRole('cell', { name: reason })).toBeVisible()

  const admin = await browser.newContext({ ignoreHTTPSErrors: true })
  const adminPage = await admin.newPage()
  try {
    await adminPage.goto('/login')
    await adminPage.locator('input[autocomplete="username"], input[type="text"]').first().fill(state().accounts.platformAdmin.login)
    await adminPage.locator('input[type="password"]').fill(state().accounts.platformAdmin.password)
    await adminPage.locator('button[type="submit"]').click()
    await adminPage.waitForURL(url => url.pathname !== '/login')
    await adminPage.goto('/platform/shares')
    await expect(adminPage.getByRole('cell', { name: reason })).toBeVisible()

    await page.getByRole('button', { name: 'Süreyi uzat' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Süreyi uzat' }).click()
    await expect(page.getByText('Paylaşım başarıyla uzatıldı.')).toBeVisible()

    await page.getByRole('button', { name: 'İptal et', exact: true }).first().click()
    await page.getByRole('dialog').getByRole('button', { name: 'İptal et' }).click()
    await expect(page.getByText('Paylaşım başarıyla iptal edildi.')).toBeVisible()

    await adminPage.reload()
    const shareRow = adminPage.getByRole('row').filter({ hasText: reason })
    await expect(shareRow.getByText('İptal edildi')).toBeVisible()
    await expect(shareRow.getByRole('button', { name: 'İndir' })).toHaveCount(0)
  } finally {
    await admin.close()
  }

  await page.goto(`/tenants/${active.tenantId}/backup-shares`)
  await expect(page.getByRole('cell', { name: reason })).toBeVisible()
  expect(errors).toEqual([])
})

test('PlatformAdmin downloading a shared Windows agent-local artifact receives the verified archive', async ({ page, browser }) => {
  test.skip(process.env.S4_DIRECT_DOWNLOAD !== '1', 'Agent-local downloads use the WebRTC direct channel, which this lab cannot establish (#277); set S4_DIRECT_DOWNLOAD=1 where it can.')
  test.setTimeout(240_000)
  const active = await activeAgent()
  const stamp = Date.now()
  const file = `s4-pshare-${stamp}.txt`
  const name = `e2e-s4-pshare-${stamp}`
  const reason = `e2e s4 platform download ${stamp}`
  await writeSourceFile(file, `platform share fixture ${stamp}`)
  const jobId = await seedFileJob(active, name, file)
  await signIn(page, active)
  await page.goto(`/tenants/${active.tenantId}/backup-jobs/${jobId}`)
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  await expect(page.getByRole('link', { name: 'Geri yükle', exact: true })).toBeVisible({ timeout: 120_000 })
  await page.getByText('Diğer', { exact: true }).first().click()
  await page.getByRole('tabpanel', { name: 'Özet' }).getByRole('button', { name: 'İndir', exact: true }).click()
  const checksum = (await page.getByRole('region', { name: /Yedeklemeyi indir/i }).locator('code').first().textContent())?.trim()
  await page.getByRole('button', { name: 'Paylaş', exact: true }).click()
  await page.getByRole('button', { name: 'Paylaşım oluştur' }).click()
  await page.getByPlaceholder('Paylaşım gerekçesini yazın').fill(reason)
  await page.getByRole('dialog').getByRole('button', { name: 'Paylaşım oluştur' }).click()
  await expect(page.getByRole('cell', { name: reason })).toBeVisible()

  const admin = await browser.newContext({ ignoreHTTPSErrors: true })
  const adminPage = await admin.newPage()
  try {
    await adminPage.goto('/login')
    await adminPage.locator('input[autocomplete="username"], input[type="text"]').first().fill(state().accounts.platformAdmin.login)
    await adminPage.locator('input[type="password"]').fill(state().accounts.platformAdmin.password)
    await adminPage.locator('button[type="submit"]').click()
    await adminPage.waitForURL(url => url.pathname !== '/login')
    await adminPage.goto('/platform/shares')
    const row = adminPage.getByRole('row').filter({ hasText: reason })
    await expect(row).toBeVisible()
    const [download] = await Promise.all([
      adminPage.waitForEvent('download', { timeout: 30_000 }),
      row.getByRole('button', { name: 'İndir' }).click(),
    ])
    const downloadedPath = await download.path()
    expect(downloadedPath).toBeTruthy()
    const body = await readFile(downloadedPath!)
    expect(createHash('sha256').update(body).digest('hex')).toBe(checksum)
  } finally {
    await admin.close()
  }
})
