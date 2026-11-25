import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { expect, test } from '@playwright/test'
import { activeAgent } from './support'

const run = promisify(execFile)
test('Windows file backup and restore preserve file contents', async ({ page }) => {
  test.setTimeout(180_000)
  const active = await activeAgent()
  await page.goto('/login')
  await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(active.login)
  await page.locator('input[type="password"]').fill('E2e-S4-Passw0rd!2026')
  await page.locator('button[type="submit"]').click()
  const file = `s4-${Date.now()}.txt`
  const prepare = `[IO.File]::WriteAllText('C:\\ProgramData\\BackupAgentData\\s4-source\\${file}','Windows S4 backup fixture')`
  await run('ssh', ['-o', 'BatchMode=yes', 'virtual-win11', 'powershell', '-NoProfile', '-EncodedCommand', Buffer.from(prepare, 'utf16le').toString('base64')])
  await page.waitForURL(new RegExp(`/tenants/${active.tenantId}`))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('requestfailed', request => { if (request.failure()?.errorText !== 'net::ERR_ABORTED') errors.push(`${request.method()} ${request.url()}: ${request.failure()?.errorText}`) })
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`) })
  await page.goto(`/tenants/${active.tenantId}/backup-jobs/new`)
  await page.getByRole('button', { name: 'Elle yapılandır' }).click()
  const name = `e2e-s4-files-${Date.now()}`
  await page.getByLabel('İş adı').fill(name)
  await page.getByLabel('Yedekleme bilgisayarı', { exact: true }).selectOption(active.agentId)
  await page.getByRole('radio', { name: 'Dosya / Klasör' }).check()
  await page.getByLabel('Dosya kaynağı referansı').fill('s4files')
  await page.getByLabel('Yedeklenecek dosya ve klasörler').fill(file)
  await page.getByRole('button', { name: 'İleri' }).click()
  await page.getByRole('button', { name: 'İleri' }).click()
  await page.getByRole('button', { name: 'Elle çalıştır' }).click()
  await page.getByRole('button', { name: 'İleri' }).click()
  await page.getByRole('button', { name: 'Kaydet', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/tenants/${active.tenantId}/backup-jobs`))
  await page.getByRole('link', { name, exact: true }).click()
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  await expect(page.getByRole('link', { name: 'Geri yükle', exact: true })).toBeVisible({ timeout: 120_000 })
  await page.getByRole('link', { name: 'Geri yükle', exact: true }).click()
  await page.getByRole('button', { name: 'Devam et →' }).click()
  await page.getByRole('button', { name: 'Devam et →' }).click()
  await page.getByRole('button', { name: 'Dosyaları geri yükle' }).click({ timeout: 90_000 })
  await expect(page).toHaveURL(/\/restores\//)
  await expect(page.getByText('Başarılı', { exact: true }).first()).toBeVisible({ timeout: 90_000 })
  const compare = `$ErrorActionPreference='Stop'; $a=Get-FileHash 'C:\\ProgramData\\BackupAgentData\\s4-source\\${file}'; $b=Get-FileHash 'C:\\ProgramData\\BackupAgentData\\s4-source\\restored\\${file}'; if ($a.Hash -ne $b.Hash) { exit 1 }`
  await run('ssh', ['-o', 'BatchMode=yes', 'virtual-win11', 'powershell', '-NoProfile', '-EncodedCommand', Buffer.from(compare, 'utf16le').toString('base64')])
  expect(errors).toEqual([])
})
