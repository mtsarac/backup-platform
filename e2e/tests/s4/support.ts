import { execFile } from 'node:child_process'
import { existsSync } from 'node:fs'
import { resolve } from 'node:path'
import { promisify } from 'node:util'
import { setTimeout as sleep } from 'node:timers/promises'
import { expect, type Page } from '@playwright/test'
import { Api } from '../../api'
import { state } from '../../state'

const run = promisify(execFile)
export const password = 'E2e-S4-Passw0rd!2026'
export const quote = (value: string) => `'${value.replaceAll("'", "''")}'`
export type Active = { tenantId: string; login: string; agentId: string }
export type DbCredential = { user: string; password: string; port: number }

/** Runs PowerShell on the Windows VM; never echoes the script, which may carry credentials. */
export async function windows(script: string): Promise<string> {
  try {
    const { stdout } = await run('ssh', ['-o', 'BatchMode=yes', 'virtual-win11', 'powershell', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(`$ErrorActionPreference='Stop'; ${script}`, 'utf16le').toString('base64')], { timeout: 90_000 })
    return stdout
  } catch { throw new Error('Windows VM command failed; inspect the VM without exposing credentials') }
}

/** The newest enrolled, online S4 Windows agent and the tenant user the enrollment spec created for it. */
export async function activeAgent(): Promise<Active> {
  const platform = await Api.signIn(state().accounts.platformAdmin)
  const tenants = await platform.call('GET', '/api/platform/tenants?search=e2e-s4-&pageSize=100')
  await platform.dispose()
  // Only enrollment tenants (`e2e-s4-<timestamp>`) have a matching `e2e.s4.<timestamp>` user; other S4 tenants would only add failed logins.
  const enrolled = tenants.items.filter((tenant: { name: string }) => /^e2e-s4-\d+$/.test(tenant.name)).sort((a: { name: string }, b: { name: string }) => b.name.localeCompare(a.name))
  for (const tenant of enrolled) {
    const login = `e2e.s4.${tenant.name.slice('e2e-s4-'.length)}`
    const api = await Api.signIn({ login, password })
    const agents = await api.call('GET', `/api/tenants/${tenant.tenantId}/agents`)
    await api.dispose()
    const agent = agents.find((item: { connectivity: string }) => item.connectivity === 'online')
    if (agent) return { tenantId: tenant.tenantId, login, agentId: agent.agentId }
  }
  throw new Error('An enrolled, online S4 Windows agent is required')
}

async function credentialFilePath(): Promise<string> {
  if (process.env.BP_CREDENTIAL_FILE) return process.env.BP_CREDENTIAL_FILE
  if (existsSync('../docs/test-cred.yaml')) return '../docs/test-cred.yaml'
  const { stdout } = await run('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'])
  return resolve(stdout.trim(), '../docs/test-cred.yaml')
}

export async function dbCredential(engine: 'postgresql' | 'mssql'): Promise<DbCredential> {
  const path = await credentialFilePath()
  const { stdout } = await run('python3', ['-c', 'import json,sys,yaml; print(json.dumps(yaml.safe_load(open(sys.argv[1]))["windows11_vm"][sys.argv[2]]))', path, engine])
  const value: unknown = JSON.parse(stdout)
  if (!value || typeof value !== 'object' || !('user' in value) || typeof value.user !== 'string' || !('password' in value) || typeof value.password !== 'string' || !('port' in value) || typeof value.port !== 'number') throw new Error('Invalid Windows database credentials')
  return { user: value.user, password: value.password, port: value.port }
}

export async function signIn(page: Page, active: Active) {
  await page.goto('/login')
  await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(active.login)
  await page.locator('input[type="password"]').fill(password)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL(new RegExp(`/tenants/${active.tenantId}`))
}

/** Collects console errors and unexpected failed or 4xx/5xx requests; assert `toEqual([])` at the end. */
export function watchErrors(page: Page): string[] {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('requestfailed', request => { if (request.failure()?.errorText !== 'net::ERR_ABORTED') errors.push(`${request.method()} ${request.url()}: ${request.failure()?.errorText}`) })
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`) })
  return errors
}

const psql = "'C:\\Program Files\\PostgreSQL\\18\\bin\\psql.exe'"
/** Runs a statement on the VM database through the real client, returning trimmed output. Credentials stay in the SSH payload. */
export async function sql(engine: 'postgresql' | 'mssql', credential: DbCredential, database: string, statement: string): Promise<string> {
  const script = engine === 'postgresql'
    ? `$env:PGPASSWORD=${quote(credential.password)}; $o=& ${psql} -h 127.0.0.1 -U ${quote(credential.user)} -d ${quote(database)} -v ON_ERROR_STOP=1 -tAc ${quote(statement)}; if ($LASTEXITCODE) { exit $LASTEXITCODE }; $o`
    : `$env:SQLCMDPASSWORD=${quote(credential.password)}; $o=& sqlcmd -S 127.0.0.1,${credential.port} -U ${quote(credential.user)} -C -b -h -1 -W -d ${quote(database)} -Q ${quote(`SET NOCOUNT ON; ${statement}`)}; if ($LASTEXITCODE) { exit $LASTEXITCODE }; $o`
  return (await windows(script)).replace(/#< CLIXML[\s\S]*$/, '').trim()
}

export async function createFixtureDatabase(engine: 'postgresql' | 'mssql', credential: DbCredential, database: string) {
  await sql(engine, credential, engine === 'postgresql' ? 'postgres' : 'master', engine === 'postgresql' ? `CREATE DATABASE ${database}` : `CREATE DATABASE [${database}]`)
  await sql(engine, credential, database, engine === 'postgresql'
    ? "CREATE TABLE s4_items(id int primary key, value text); INSERT INTO s4_items VALUES (1,'one'),(2,'two'),(3,'three')"
    : "CREATE TABLE s4_items(id int primary key, value nvarchar(40)); INSERT INTO s4_items VALUES (1,'one'),(2,'two'),(3,'three')")
}

export async function dropDatabase(engine: 'postgresql' | 'mssql', credential: DbCredential, database: string) {
  await sql(engine, credential, engine === 'postgresql' ? 'postgres' : 'master', engine === 'postgresql' ? `DROP DATABASE IF EXISTS ${database}` : `IF DB_ID('${database}') IS NOT NULL BEGIN ALTER DATABASE [${database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [${database}] END`)
}

async function openCredentialForm(page: Page) {
  const userField = page.getByLabel('Veritabanı kullanıcı adı')
  if (await userField.isVisible()) return
  await page.getByText('Giriş bilgilerini değiştir', { exact: true }).click()
  await expect(userField).toBeVisible()
}

async function waitManualCooldown(active: Active) {
  const api = await Api.signIn({ login: active.login, password })
  try {
    for (let i = 0; i < 15; i++) {
      const list = await api.call('GET', `/api/tenants/${active.tenantId}/discovery/agents`)
      const status = list.items.find((item: { agentId: string }) => item.agentId === active.agentId)
      if (!status || status.scanState === 'Idle') break
      await sleep(1000)
    }
  } finally { await api.dispose() }
  await sleep(5500)
}

/** Exercises UI discovery inventory, credential validation + auth failure + happy path, job creation, and execution. */
export async function discoverConfigureAndRun(page: Page, active: Active, engine: 'postgresql' | 'mssql', credential: DbCredential, database: string, name: string) {
  await waitManualCooldown(active)
  await page.goto(`/tenants/${active.tenantId}/agents/databases`)
  const linkPattern = engine === 'postgresql' ? /PostgreSQL/ : /SQL Server/
  await expect(page.getByRole('link', { name: linkPattern }).first()).toBeVisible({ timeout: 30_000 })
  await page.getByRole('link', { name: linkPattern }).first().click()
  await expect(page).toHaveURL(/\/discovery\/instances\//)

  await openCredentialForm(page)
  await page.getByLabel('Veritabanı kullanıcı adı').fill(credential.user)
  await page.getByLabel('Veritabanı parolası').fill('')
  await page.getByRole('button', { name: 'Kaydet ve doğrula' }).click()
  await expect(page.getByText('Veritabanı parolasını girin.')).toBeVisible()

  const encrypt = page.getByRole('checkbox', { name: 'Bağlantıyı şifrele' })
  if (engine === 'postgresql') await encrypt.uncheck()
  else await encrypt.check()
  await page.getByLabel('Veritabanı parolası').fill('wrong-password-s4')
  await page.getByRole('button', { name: 'Kaydet ve doğrula' }).click()

  if (engine === 'mssql') {
    const trustSummary = page.getByText('Bu sunucunun sertifikasına yine de güven')
    const failedPill = page.getByText('Kimlik doğrulaması başarısız', { exact: true })
    await expect(trustSummary.or(failedPill).first()).toBeVisible({ timeout: 60_000 })
    if (await trustSummary.isVisible()) {
      await waitManualCooldown(active)
      await trustSummary.click()
      await page.getByRole('button', { name: 'Güven ve yeniden dene' }).click()
    }
  }
  await expect(page.getByText('Kimlik doğrulaması başarısız', { exact: true })).toBeVisible({ timeout: 60_000 })
  await expect(page.getByText('Giriş bilgilerini kontrol edip yeniden kaydedin.')).toBeVisible({ timeout: 60_000 })

  await waitManualCooldown(active)
  await openCredentialForm(page)
  await page.getByLabel('Veritabanı kullanıcı adı').fill(credential.user)
  await page.getByLabel('Veritabanı parolası').fill(credential.password)
  if (engine === 'postgresql') await encrypt.uncheck()
  else await encrypt.check()
  await page.getByRole('button', { name: 'Kaydet ve doğrula' }).click()
  await expect(page.getByText('Kimlik doğrulandı')).toBeVisible({ timeout: 60_000 })
  const dbCheckbox = page.getByRole('checkbox', { name: `${database} seç` })
  await expect(dbCheckbox).toBeVisible({ timeout: 60_000 })
  await dbCheckbox.check()
  await page.getByRole('button', { name: /Yedekleme işi oluştur/ }).click()

  await expect(page).toHaveURL(/\/backup-jobs\/new/)
  await expect(page.getByText('Seçilen kaynak')).toBeVisible()
  await page.getByLabel('İş adı').fill(name)
  await page.getByRole('button', { name: 'İleri' }).click()
  await page.getByRole('button', { name: 'İleri' }).click()
  await page.getByRole('button', { name: 'Elle çalıştır' }).click()
  await page.getByRole('button', { name: 'İleri' }).click()
  await page.getByRole('button', { name: 'Kaydet', exact: true }).click()
  await page.getByRole('link', { name, exact: true }).click()
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  await expect(page.getByRole('link', { name: 'Geri yükle', exact: true })).toBeVisible({ timeout: 120_000 })
}

/** Seeds a file job on the S4 file source through the API; `cron` makes it scheduled, otherwise it is manual. */
export async function seedFileJob(active: Active, name: string, path: string, cron?: string): Promise<string> {
  const api = await Api.signIn({ login: active.login, password })
  try {
    const job = await api.call('POST', `/api/tenants/${active.tenantId}/backup-jobs`, {
      name, agentId: active.agentId, overlapPolicy: 'Skip',
      ...(cron ? { schedule: { enabled: true, cronExpression: cron, timeZone: 'UTC', missedRunGraceSeconds: 1800, overlapPolicy: 'Skip' } } : {}),
      configuration: { engine: 'file-system', database: 's4files', host: 'localhost', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: false, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off', files: { paths: [path], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' }, destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true, storageTarget: 'default' }] },
    }, [200, 201], { 'Idempotency-Key': `s4-${name}` })
    return job.id
  } finally { await api.dispose() }
}

export const sourceDirectory = 'C:\\ProgramData\\BackupAgentData\\s4-source'
export async function writeSourceFile(file: string, content: string) {
  await windows(`[IO.File]::WriteAllText(${quote(`${sourceDirectory}\\${file}`)}, ${quote(content)})`)
}

/** (Re)creates the `s4files` file source on the agent service: an installer replace drops service environment, so enrollment re-adds it. */
export async function configureFileSource() {
  await windows(`
    New-Item -ItemType Directory -Force -Path ${quote(sourceDirectory)} | Out-Null
    $key = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\BackupAgent'
    $env = @((Get-ItemProperty $key).Environment | Where-Object { $_ -notlike 'Agent__Backup__FileSources__s4files__*' })
    Set-ItemProperty $key Environment ($env + ${quote('Agent__Backup__FileSources__s4files__RootDirectory=' + sourceDirectory)})
    $acl = Get-Acl ${quote(sourceDirectory)}
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('NT SERVICE\\BackupAgent', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl ${quote(sourceDirectory)} $acl
    Restart-Service BackupAgent`)
}

/** The installer refuses to replace an agent whose credential store holds anything but identity files; agent logs live there. */
export async function clearAgentLogs() {
  await windows("$s = Get-Service BackupAgent -ErrorAction SilentlyContinue; if ($s) { Stop-Service BackupAgent -Force }; if (Test-Path 'C:\\ProgramData\\BackupAgent\\logs') { Remove-Item 'C:\\ProgramData\\BackupAgent\\logs' -Recurse -Force }")
}
