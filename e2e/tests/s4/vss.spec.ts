import { spawn } from 'node:child_process'
import { expect, test } from '@playwright/test'
import { activeAgent, quote, signIn, sourceDirectory, watchErrors, windows } from './support'

test('VSS backs up and restores an exclusively locked Windows file', async ({ page }) => {
  test.setTimeout(240_000)
  const active = await activeAgent()
  const file = `s4-vss-${Date.now()}.txt`
  const path = `${sourceDirectory}\\${file}`
  await windows(`
    $key = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\BackupAgent'
    $env = @((Get-ItemProperty $key).Environment | Where-Object { $_ -notmatch '^(Features__windowsVss|Agent__Backup__FileSources__s4vss__)' })
    Set-ItemProperty $key Environment ($env + @('Features__windowsVss=true', ${quote(`Agent__Backup__FileSources__s4vss__RootDirectory=${sourceDirectory}`)}, 'Agent__Backup__FileSources__s4vss__SnapshotKind=Vss'))
    if (-not (Get-LocalGroupMember 'Backup Operators' | Where-Object Name -eq 'NT SERVICE\\BackupAgent')) { Add-LocalGroupMember 'Backup Operators' 'NT SERVICE\\BackupAgent' }
    Restart-Service BackupAgent
    [IO.File]::WriteAllText(${quote(path)}, 'VSS locked file fixture')`)
  const pidPath = `${path}.pid`
  const locker = `$f=[IO.File]::Open(${quote(path)},'Open','ReadWrite','None'); try { [IO.File]::WriteAllText(${quote(pidPath)}, "$PID"); Start-Sleep -Seconds 600 } finally { $f.Dispose() }`
  const process = spawn('ssh', ['-o', 'BatchMode=yes', 'virtual-win11', 'powershell', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(locker, 'utf16le').toString('base64')], { stdio: 'ignore' })
  try {
    await expect(async () => {
      const locked = await windows(`if (-not (Test-Path ${quote(pidPath)})) { throw 'Locker not ready' }; try { $f=[IO.File]::Open(${quote(path)},'Open','ReadWrite','None'); $f.Dispose(); 'unlocked' } catch { 'locked' }`)
      expect(locked.trim()).toBe('locked')
    }).toPass({ timeout: 15_000 })
    await signIn(page, active)
    const errors = watchErrors(page)
    await page.goto(`/tenants/${active.tenantId}/backup-jobs/new`)
    await page.getByRole('button', { name: 'Elle yapılandır' }).click()
    const name = `e2e-s4-vss-${Date.now()}`
    await page.getByLabel('İş adı').fill(name)
    await page.getByLabel('Yedekleme bilgisayarı', { exact: true }).selectOption(active.agentId)
    await page.getByRole('radio', { name: 'Dosya / Klasör' }).check()
    await page.getByLabel('Dosya kaynağı referansı').fill('s4vss')
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
    await expect(page.getByText('Başarılı', { exact: true }).first()).toBeVisible({ timeout: 90_000 })
    expect(errors).toEqual([])
  } finally {
    await windows(`if (Test-Path ${quote(pidPath)}) { Stop-Process -Id ([int](Get-Content ${quote(pidPath)})); Remove-Item ${quote(pidPath)} }`)
    process.kill()
  }
  await expect(async () => {
    const unlocked = await windows(`$f=[IO.File]::Open(${quote(path)},'Open','ReadWrite','None'); $f.Dispose(); 'unlocked'`)
    expect(unlocked.trim()).toBe('unlocked')
  }).toPass({ timeout: 15_000 })
  await windows(`$a=Get-FileHash ${quote(path)} -Algorithm SHA256; $b=Get-FileHash ${quote(`${sourceDirectory}\\restored\\${file}`)} -Algorithm SHA256; if ($a.Hash -ne $b.Hash) { throw 'SHA-256 mismatch' }`)
})
