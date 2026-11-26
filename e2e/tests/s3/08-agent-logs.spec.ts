import { readFile } from 'node:fs/promises'
import { randomUUID } from 'node:crypto'
import { setTimeout as sleep } from 'node:timers/promises'
import { expect, test } from '@playwright/test'
import { type Api, apiUrl, createEnrollmentToken } from '../../api'
import { seedJob, seedS3, vm, waitOnline } from './fixture'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
let logsSupported = false
let logAgentId = ''
let logJobId = ''
let logRunId = ''
const suffix = Date.now().toString(36)
const unitName = `e2e-s3-log-agent-${suffix}`
const workDir = `/opt/backup-agent-s3-logs-${suffix}`
const logAgentName = `e2e-s3-logs-${suffix}`
const logJobName = `e2e-s3-logjob-${suffix}`

test.describe.configure({ mode: 'serial', timeout: 180_000 })

test.beforeAll(async () => {
  test.setTimeout(180_000)
  ;({ tenantId, tenant } = await seedS3())
  const probe = await tenant.call('GET', `/api/tenants/${tenantId}/agent-logs`, undefined, [200, 404])
  logsSupported = Array.isArray(probe)
  if (!logsSupported) return

  const parsed = new URL(apiUrl)
  const hostPort = `${parsed.hostname}:${parsed.port || '443'}`
  const token = await createEnrollmentToken(tenant, tenantId)

  // Install the target stack's TLS cert into Fedora CA trust and launch an isolated BackupPlatform.Agent.Host unit in /tmp
  // so we never disturb the shared backup-platform-agent.service on Fedora.
  vm(`set -e
openssl s_client -connect '${hostPort}' -servername '${parsed.hostname}' </dev/null 2>/dev/null | sed -ne '/-BEGIN CERTIFICATE-/,/-END CERTIFICATE-/p' | sudo -n tee /etc/pki/ca-trust/source/anchors/e2e-s3-logs.pem >/dev/null
sudo -n update-ca-trust
id -u backup-agent >/dev/null 2>&1 || sudo -n useradd --system --user-group --no-create-home --shell /usr/sbin/nologin backup-agent
sudo -n rm -rf '${workDir}'
sudo -n mkdir -p '${workDir}/pkg' '${workDir}/store' '${workDir}/staging' '${workDir}/backups'
sudo -n curl --fail --silent --show-error --proto '=https' --tlsv1.2 '${apiUrl}/api/agent-dist/packages/linux-x64' -o '${workDir}/agent.tar.gz'
sudo -n tar -xzf '${workDir}/agent.tar.gz' -C '${workDir}/pkg' --strip-components=1
sudo -n chmod -R a+rX '${workDir}/pkg'
sudo -n chmod 755 '${workDir}' '${workDir}/pkg/bin/BackupPlatform.Agent.Host'
sudo -n chown -R backup-agent:backup-agent '${workDir}/store' '${workDir}/staging' '${workDir}/backups'
sudo -n chmod 700 '${workDir}/store' '${workDir}/staging' '${workDir}/backups'
sudo -n BACKUP_AGENT_ENROLLMENT_TOKEN='${token}' runuser --user backup-agent --preserve-environment -- '${workDir}/pkg/bin/BackupPlatform.Agent.Host' enroll --server '${apiUrl}' --store-directory '${workDir}/store' --name '${logAgentName}' --non-interactive
sudo -n systemd-run --unit='${unitName}' --uid=backup-agent --gid=backup-agent --working-directory='${workDir}/pkg/bin' \\
  --setenv="Agent__Backend__BaseUri=${apiUrl}" \\
  --setenv="Agent__CredentialStoreDirectory=${workDir}/store" \\
  --setenv="Agent__Backup__Staging__Mapping=SameHost" \\
  --setenv="Agent__Backup__Staging__AgentDirectory=${workDir}/staging" \\
  --setenv="Agent__Backup__LocalStorage__RootDirectory=${workDir}/backups" \\
  '${workDir}/pkg/bin/BackupPlatform.Agent.Host'`)

  logAgentId = await waitOnline(tenant, tenantId, logAgentName, 90_000)

  // Seed and trigger a failing backup job (unconfigured file source) so the real Fedora agent emits both Information and Warning logs
  logJobId = await seedJob(tenant, tenantId, {
    name: logJobName,
    agentId: logAgentId,
    overlapPolicy: 'Skip',
    retryPolicy: { maxAttempts: 1, initialDelaySeconds: 5, maxDelaySeconds: 10, backoff: 'Exponential', jitter: 0 },
    configuration: {
      engine: 'file-system', database: 'unconfigured_source_for_log_test', host: 'localhost', port: null, integratedSecurity: true,
      databaseCredentialReference: null, encrypt: false, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
      files: { paths: ['docs'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
      destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
    },
  })
  const started = await tenant.call('POST', `/api/tenants/${tenantId}/backup-jobs/${logJobId}/runs`, {}, [200, 201, 202], { 'Idempotency-Key': randomUUID() })
  logRunId = started.id

  const deadline = Date.now() + 60_000
  while (Date.now() < deadline) {
    const logs: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/agent-logs?agentId=${logAgentId}`)
    if (logs.some(l => l.level === 'Information') && logs.some(l => l.level === 'Warning' && l.runId === logRunId)) break
    await sleep(2000)
  }
})

test.afterAll(async () => {
  if (logsSupported) {
    vm(`sudo -n systemctl stop '${unitName}' 2>/dev/null || true; sudo -n systemctl reset-failed '${unitName}' 2>/dev/null || true; sudo -n rm -rf '${workDir}'`)
    if (logJobId) await tenant.call('DELETE', `/api/tenants/${tenantId}/backup-jobs/${logJobId}`, undefined, [200, 204]).catch(() => {})
    if (logAgentId) await tenant.call('POST', `/api/tenants/${tenantId}/agents/${logAgentId}/revoke`, {}, [200, 204, 409]).catch(() => {})
  }
  await tenant?.dispose()
})

test('real Fedora agent logs are displayed, filtered by level/text/date, live-tailed, downloaded, and scoped on job and run pages (#230)', async ({ page }) => {
  test.skip(!logsSupported, 'Target stack predates #246 (/agent-logs 404); run against rebuilt stack or E2E_BASE_URL=https://192.168.122.1:6173 E2E_API_URL=https://192.168.122.1:6252')

  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `agents/${logAgentId}`))

  const logs = page.getByRole('region', { name: 'Bilgisayar günlükleri' })
  await expect(logs.getByRole('cell', { name: 'Bilgi' }).first()).toBeVisible({ timeout: 30_000 })
  await expect(logs.getByRole('cell', { name: 'Uyarı' }).first()).toBeVisible()

  // Filter by level
  await logs.getByLabel('Seviye').selectOption('Warning')
  await expect(logs.getByRole('cell', { name: 'Bilgi' })).toHaveCount(0)
  await expect(logs.getByRole('cell', { name: 'Uyarı' }).first()).toBeVisible()

  // Filter by text (empty state + match)
  await logs.getByLabel('Metin').fill('kesinlikle-eslesmeyen-metin-xyz')
  await expect(logs.getByText('Bu filtrelerle günlük kaydı bulunamadı.')).toBeVisible()
  await logs.getByLabel('Metin').fill('')
  await logs.getByLabel('Seviye').selectOption('')

  // Filter by future start date -> empty -> clear
  await logs.getByLabel('Başlangıç').fill('2099-01-01T00:00')
  await expect(logs.getByText('Bu filtrelerle günlük kaydı bulunamadı.')).toBeVisible()
  await logs.getByLabel('Başlangıç').fill('')
  await expect(logs.getByRole('cell', { name: 'Bilgi' }).first()).toBeVisible()

  // Live tail receives a new real startup log entry when the Fedora unit restarts
  const beforeCount = await logs.locator('tbody tr').count()
  await logs.getByLabel('Canlı takip').check()
  vm(`sudo -n systemctl restart '${unitName}'`)
  await expect.poll(async () => logs.locator('tbody tr').count(), { timeout: 30_000, intervals: [2000] }).toBeGreaterThan(beforeCount)

  // Download agent-logs.json and verify real JSON entries
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    logs.getByRole('button', { name: 'İndir' }).click(),
  ])
  expect(download.suggestedFilename()).toBe('agent-logs.json')
  const exported = JSON.parse(await readFile((await download.path())!, 'utf8'))
  expect(Array.isArray(exported)).toBe(true)
  expect(exported.length).toBeGreaterThan(0)
  expect(exported.every((e: any) => e.agentId === logAgentId)).toBe(true)

  // Scoped log panels on job detail and run detail show the warning log from the failed run
  for (const sub of [`backup-jobs/${logJobId}`, `backup-runs/${logRunId}`]) {
    await page.goto(tenantPath(tenantId, sub))
    const scopedLogs = page.getByRole('region', { name: 'Bilgisayar günlükleri' })
    await expect(scopedLogs.getByRole('cell', { name: 'Uyarı' }).first(), sub).toBeVisible()
  }
  w.expectClean()
})
