import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { state } from '../../state'
import { english, signIn, watch, world, type World } from './support'

let w: World
test.beforeAll(async () => { w = await world() })
test.beforeEach(async ({ context }) => { await english(context) })

async function violations(page: Page) {
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  return result.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} node(s) e.g. ${v.nodes[0]?.target.join(' ')}`)
}

/** One real visit per route: a main landmark, exactly one h1, no axe violations (contrast included) and a clean console. */
async function sweep(page: Page, paths: string[]) {
  const health = watch(page)
  const failures: string[] = []
  for (const path of paths) {
    await page.goto(path)
    await expect(page.getByRole('main'), `${path} main`).toBeVisible()
    await expect(page.getByRole('heading', { level: 1 }), `${path} h1 count`).toHaveCount(1)
    await page.waitForLoadState('networkidle')
    for (const violation of await violations(page)) failures.push(`${path}: ${violation}`)
  }
  expect(failures, 'axe violations per route').toEqual([])
  health.expectClean()
}

test.describe('a11y route sweep', () => {
  test('every tenant route of the populated tenant', async ({ page }) => {
    test.setTimeout(180_000)
    await signIn(page, w.populated.account)
    const base = `/tenants/${w.populated.tenantId}`
    await sweep(page, [
      '', '/agents', '/agents/databases', `/agents/${w.agent.agentId}`, '/enrollment', '/first-backup',
      '/backup-jobs', '/backup-jobs/new', `/backup-jobs/${w.jobId}`,
      '/backup-runs', `/backup-runs/${w.runs.view}`, `/backup-runs/${w.runs.view}/restore`, '/restores', '/backup-shares',
      '/storage', '/storage/connections/new', '/settings', '/settings/recovery-key', '/settings/bandwidth',
    ].map((path) => base + path))
  })

  test('every tenant route of the empty tenant (empty states)', async ({ page }) => {
    test.setTimeout(180_000)
    await signIn(page, w.empty.account)
    const base = `/tenants/${w.empty.tenantId}`
    await sweep(page, ['', '/agents', '/enrollment', '/first-backup', '/backup-jobs', '/backup-runs', '/restores', '/backup-shares', '/storage', '/settings'].map((path) => base + path))
  })

  test('every platform route', async ({ page }) => {
    test.setTimeout(180_000)
    const admin = state().accounts.platformAdmin
    await signIn(page, admin)
    await sweep(page, [
      '/platform', '/platform/tenants', `/platform/tenants/${w.populated.tenantId}`, `/platform/tenants/${w.populated.tenantId}/agents/${w.agent.agentId}`,
      '/platform/plans', '/platform/storage', '/platform/storage/connections', '/platform/quota-policies', '/platform/users',
      '/platform/agents', '/platform/audit', '/platform/shares', '/platform/notification-transports', '/platform/agent-settings',
    ])
  })
})
