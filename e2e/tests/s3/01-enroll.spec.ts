import { expect, test } from '@playwright/test'
import { type Api, apiUrl, enableAgentEnrollment } from '../../api'
import { state } from '../../state'
import { AGENT_NAME, resetFedoraForEnrollment, seedS3, vm } from './fixture'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
let consumedToken = ''

test.describe.configure({ mode: 'serial', timeout: 240_000 })

test.beforeAll(async () => {
  ;({ tenantId, tenant } = await seedS3())
  await resetFedoraForEnrollment(tenant, tenantId)
})
test.afterAll(async () => { await tenant.dispose() })

test('Add computer dialog validates input, enrolls the Fedora agent via install.sh, clears the secret, and shows Online', async ({ page }) => {
  const w = watch(page)
  await signIn(page)
  w.reset()
  await page.goto(tenantPath(tenantId, 'agents'))
  await expect(page.getByText('Henüz yedekleme bilgisayarı eklenmedi.')).toBeVisible()
  await page.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' }).first().click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: 'İleri' })).toBeDisabled()
  await dialog.getByLabel('Bilgisayarın adı').fill(AGENT_NAME)
  await dialog.getByLabel('İşletim sistemi').selectOption('Linux')
  await dialog.getByRole('button', { name: 'İleri' }).click()
  await dialog.getByRole('button', { name: 'Kayıt anahtarı oluştur' }).click()
  const command = await dialog.getByLabel('Tek kullanımlık kurulum komutu').inputValue()
  expect(command).toContain('install.sh')
  expect(command).toContain(`--name '${AGENT_NAME}'`)
  const tokenMatch = command.match(/--token '([^']+)'/)
  expect(tokenMatch?.[1]).toBeTruthy()
  consumedToken = tokenMatch![1]
  await expect(dialog.getByRole('status')).toContainText('bekleniyor')
  vm(command)
  await expect(dialog.getByRole('status')).toContainText('Çevrimiçi', { timeout: 120_000 })
  await expect(dialog.getByLabel('Tek kullanımlık kurulum komutu')).toHaveCount(0)
  await dialog.getByRole('button', { name: 'Tamam' }).click()
  await expect(page.getByRole('link', { name: AGENT_NAME })).toBeVisible()
  w.expectClean()
})

test('restarting backup-agent.service preserves AgentId and Online state; non-interactive install.sh refuses overwrite', async ({ page }) => {
  const w = watch(page)
  await signIn(page)
  w.reset()
  await page.goto(tenantPath(tenantId, 'agents'))
  await page.getByRole('link', { name: AGENT_NAME }).click()
  await expect(page).toHaveURL(/\/agents\/[0-9a-f-]+$/)
  const agentIdBefore = page.url().split('/').at(-1)!
  await expect(page.getByText('Çevrimiçi').first()).toBeVisible()

  vm('sudo -n systemctl restart backup-agent.service && sleep 2 && sudo -n systemctl is-active --quiet backup-agent.service')
  await page.reload()
  await expect(page).toHaveURL(new RegExp(`/agents/${agentIdBefore}$`))
  await expect(page.getByText('Çevrimiçi').first()).toBeVisible({ timeout: 30_000 })

  const refusal = vm(`curl --fail --silent --proto '=https' --tlsv1.2 '${apiUrl}/api/agent-dist/install.sh' | sudo -n bash -s -- --server '${apiUrl}' --token '11111111111111111111111111111111.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' --name 'dup' --language tr 2>&1 || true`)
  expect(refusal).toContain('Mevcut bir ajan kurulumu bulundu')
  w.expectClean()
})

test('reused enrollment token is rejected, pending tokens can be revoked, and disabling enrollment blocks new keys', async ({ page }) => {
  expect(consumedToken).not.toBe('')
  const reusedOut = vm(`set +e
dir=/tmp/e2e-s3-reused-$$
sudo -n install -d -m 700 -o backup-agent -g backup-agent "$dir"
sudo -n BACKUP_AGENT_ENROLLMENT_TOKEN='${consumedToken}' runuser --user backup-agent --preserve-environment -- /opt/backup-agent/bin/BackupPlatform.Agent.Host enroll --server '${apiUrl}' --store-directory "$dir" --name 'reused-agent' --non-interactive 2>&1
rc=$?
sudo -n rm -rf "$dir"
echo "EXIT:$rc"`)
  expect(reusedOut).not.toContain('EXIT:0')

  const w = watch(page)
  await signIn(page)
  w.reset()
  try {
    await page.goto(tenantPath(tenantId, 'enrollment'))
    await page.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Bilgisayarın adı').fill('e2e-s3-pending-token')
    await dialog.getByLabel('İşletim sistemi').selectOption('Linux')
    await dialog.getByRole('button', { name: 'İleri' }).click()
    await dialog.getByRole('button', { name: 'Kayıt anahtarı oluştur' }).click()
    await expect(dialog.getByLabel('Tek kullanımlık kurulum komutu')).toBeVisible()
    await dialog.getByRole('button', { name: 'Kapat' }).click()

    await expect(page.getByRole('button', { name: /kayıt anahtarını iptal et/ }).first()).toBeVisible()
    await page.getByRole('button', { name: /kayıt anahtarını iptal et/ }).first().click()
    await page.getByRole('dialog').getByRole('button', { name: 'İptali onayla' }).click()
    await expect(page.getByText('Bekleyen kayıt anahtarı yok.')).toBeVisible()

    const toggle = page.getByRole('switch', { name: 'Yeni bilgisayar kayıtlarına izin ver' })
    await toggle.click()
    await expect(page.getByText('Yeni yedekleme bilgisayarı eklenemez.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' })).toBeDisabled()

    await toggle.click()
    await expect(page.getByText('Yeni yedekleme bilgisayarı eklenebilir.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Yedekleme bilgisayarı ekle' })).toBeEnabled()
    w.expectClean()
  } finally {
    const platform = await (await import('../../api')).Api.signIn(state().accounts.platformAdmin)
    await enableAgentEnrollment(platform, tenantId)
    await platform.dispose()
  }
})
