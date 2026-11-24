import { expect, type Page, type BrowserContext } from '@playwright/test'
import { Api } from '../../api'
import { state, type Account } from '../../state'
import { createJob, enrollAgent, runOnce, seedTenant, type ProtocolAgent, type S5Tenant } from '../../protocol-agent'

export const rand = Math.random().toString(36).slice(2, 7)

export type World = {
  rand: string
  populated: S5Tenant
  empty: S5Tenant
  agent: ProtocolAgent
  jobId: string
  jobName: string
  hostName: string
  artifacts: { view: string; remove: string; share: string }
  runs: { view: string; remove: string; share: string; failed: string }
}

let cached: Promise<World> | undefined

/** Seeds once per worker process: one tenant with an agent, a job, three artifacts and a failed run; one tenant with nothing. */
export function world(): Promise<World> {
  cached ??= (async () => {
    const platform = await Api.signIn(state().accounts.platformAdmin)
    const populated = await seedTenant(platform, `${rand}p`)
    const empty = await seedTenant(platform, `${rand}e`)
    const hostName = `s5-host-${rand}`
    const jobName = `s5-job-${rand}`
    const agent = await enrollAgent(populated, hostName)
    const jobId = await createJob(populated, agent, jobName)
    const view = await runOnce(populated, agent, jobId, { bytes: 2_097_152 })
    const remove = await runOnce(populated, agent, jobId, { bytes: 1_048_576 })
    const share = await runOnce(populated, agent, jobId, { bytes: 524_288 })
    const failed = await runOnce(populated, agent, jobId, { failureCode: 'InvalidConfiguration' })
    const list = await populated.tenant.call('GET', `/api/tenants/${populated.tenantId}/backup-artifacts?pageSize=50`)
    const byRun = (run: string) => list.items.find((a: any) => a.runId === run).id as string
    return { rand, populated, empty, agent, jobId, jobName, hostName, artifacts: { view: byRun(view), remove: byRun(remove), share: byRun(share) }, runs: { view, remove, share, failed } }
  })()
  return cached
}

/** All specs read English text; the app stores the language in localStorage. */
export async function english(context: BrowserContext) {
  await context.addInitScript(() => localStorage.setItem('bp_language', 'en'))
}

export async function signIn(page: Page, account: Account) {
  await page.goto('/login')
  await page.locator('input[type="text"]').first().fill(account.login)
  await page.locator('input[type="password"]').fill(account.password)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL((u) => u.pathname !== '/login')
}

/** Collects console errors/warnings, uncaught errors and failed (>=400) network responses. Expected failures can be allowed by status+path regex. */
export function watch(page: Page, allow: { status: number; path: RegExp }[] = []) {
  const problems: string[] = []
  allow = [{ status: 401, path: /^\/api\/me$/ }, ...allow] // the signed-out probe on /login
  page.on('console', (m) => { if ((m.type() === 'error' || m.type() === 'warning') && !/status of 401/.test(m.text())) problems.push(`console.${m.type()}: ${m.text().slice(0, 200)}`) })
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('response', (r) => {
    const url = new URL(r.url())
    if (r.status() >= 400 && !allow.some((a) => a.status === r.status() && a.path.test(url.pathname))) problems.push(`HTTP ${r.status()} ${r.request().method()} ${url.pathname}`)
  })
  page.on('requestfailed', (r) => r.failure()?.errorText !== 'net::ERR_ABORTED' && problems.push(`requestfailed ${r.url()} ${r.failure()?.errorText}`))
  return { problems, expectClean: () => expect(problems, 'console errors / failed requests').toEqual([]) }
}

export const tenantUrl = (t: S5Tenant, path = '') => `/tenants/${t.tenantId}${path}`
