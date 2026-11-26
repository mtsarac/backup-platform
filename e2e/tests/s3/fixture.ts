// S3 (Linux agent) fixture: dedicated tenant and user so other slices sharing the stack stay untouched. Idempotent, API + SSH setup only.
import { randomUUID } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { setTimeout as sleep } from 'node:timers/promises'
import { Api, apiUrl, createEnrollmentToken, enableAgentEnrollment, ensureTenant, ensureTenantUser, ensureUnlimitedPlan } from '../../api'
import { state, type Account } from '../../state'

export const TENANT_NAME = 'e2e-s3 tenant'
export const AGENT_NAME = 'e2e-s3-fedora'
export const account: Account = { login: 'e2e.s3.admin', password: 'E2e-Passw0rd!2026' }
export const VM = 'virtual-fedora'

export async function seedS3() {
  const platform = await Api.signIn(state().accounts.platformAdmin)
  const tenantId = await ensureTenant(platform, TENANT_NAME)
  const userId = await ensureTenantUser(platform, [tenantId], account, { displayName: 'E2E S3 Admin' })
  await platform.call('POST', `/api/platform/tenants/${tenantId}/memberships`, { userId }, [200, 201, 409])
  await ensureUnlimitedPlan(platform, tenantId, 'E2E Unlimited')
  await enableAgentEnrollment(platform, tenantId)
  await platform.dispose()
  return { tenantId, tenant: await Api.signIn(account) }
}

/** Runs a bash script on the Fedora VM via stdin (the VM login shell is fish, so always invoke bash -s). */
export const vm = (script: string, timeoutMs = 300_000) =>
  execFileSync('ssh', ['-o', 'BatchMode=yes', VM, 'bash', '-s'], { input: script, encoding: 'utf8', timeout: timeoutMs })

export async function waitOnline(tenant: Api, tenantId: string, name = AGENT_NAME, timeoutMs = 90_000): Promise<string> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const agents: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/agents`)
    const match = agents.find(a => a.hostName === name && a.connectivity === 'online')
    if (match) return match.agentId
    await sleep(2000)
  }
  throw new Error(`Agent ${name} did not report online within ${timeoutMs} ms`)
}

/** Prepares an empty state for 01-enroll.spec.ts: revokes prior active agents in the S3 tenant and removes any prior install on Fedora. */
export async function resetFedoraForEnrollment(tenant: Api, tenantId: string) {
  const agents: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/agents`)
  for (const agent of agents) await tenant.call('POST', `/api/tenants/${tenantId}/agents/${agent.agentId}/revoke`, {}, [200, 204])
  vm(`set -e
sudo -n systemctl start postgresql
if sudo -n systemctl cat backup-platform-agent.service >/dev/null 2>&1; then
  sudo -n systemctl disable --now backup-platform-agent.service || true
fi
sudo -n rm -rf /etc/systemd/system/backup-platform-agent.service /etc/systemd/system/backup-platform-agent.service.d /opt/backup-platform-agent /var/lib/backup-platform-agent /etc/backup-platform-agent
sudo -n systemctl daemon-reload
sudo -n systemctl reset-failed backup-platform-agent.service 2>/dev/null || true
if getent passwd backup-agent >/dev/null; then sudo -n userdel backup-agent || true; fi
if getent group backup-agent >/dev/null; then sudo -n groupdel backup-agent || true; fi`)
}

/** Ensures AGENT_NAME is installed and online (allows running any 02..07 spec standalone without running 01-enroll first). */
export async function ensureAgent(tenant: Api, tenantId: string): Promise<string> {
  vm('sudo -n systemctl start postgresql && if sudo -n systemctl cat backup-platform-agent.service >/dev/null 2>&1; then sudo -n systemctl start backup-platform-agent.service || true; fi')
  const existing: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/agents`)
  const online = existing.find(a => a.hostName === AGENT_NAME && a.connectivity === 'online')
  if (online) return online.agentId
  for (const a of existing.filter(x => x.hostName === AGENT_NAME)) {
    await tenant.call('POST', `/api/tenants/${tenantId}/agents/${a.agentId}/revoke`, {}, [200, 204])
  }
  const token = await createEnrollmentToken(tenant, tenantId)
  vm(`curl --fail --silent --show-error --proto '=https' --tlsv1.2 --location '${apiUrl}/api/agent-dist/install-agent.sh' | sudo -n bash -s -- --server '${apiUrl}' --token '${token}' --name '${AGENT_NAME}' --language tr`)
  return waitOnline(tenant, tenantId, AGENT_NAME, 90_000)
}

/** Enrolls an isolated temporary agent identity in /tmp on Fedora without touching backup-platform-agent.service (for revoke/offline failure tests). */
export async function enrollTransientAgent(tenant: Api, tenantId: string, name: string, opts: { sendHeartbeat?: boolean } = {}) {
  const token = await createEnrollmentToken(tenant, tenantId)
  const dir = `/tmp/e2e-s3-agent-${randomUUID().slice(0, 8)}`
  vm(`set -e
sudo -n install -d -m 700 -o backup-agent -g backup-agent ${dir}
sudo -n BACKUP_AGENT_ENROLLMENT_TOKEN='${token}' runuser --user backup-agent --preserve-environment -- /opt/backup-platform-agent/current/BackupPlatform.Agent.Host enroll --server '${apiUrl}' --store-directory '${dir}' --name '${name}' --non-interactive
${opts.sendHeartbeat ? `sudo -n timeout 5s runuser --user backup-agent -- env Agent__Backend__BaseUri='${apiUrl}' Agent__CredentialStoreDirectory='${dir}' /opt/backup-platform-agent/current/BackupPlatform.Agent.Host >/dev/null 2>&1 || true` : ''}`)
  const agents: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/agents`)
  const agent = agents.find(a => a.hostName === name)
  if (!agent) throw new Error(`Transient agent ${name} not found after enroll`)
  return {
    agentId: agent.agentId as string,
    enrolledAt: Date.now(),
    cleanup: () => vm(`sudo -n rm -rf ${dir}`),
  }
}

export async function seedJob(tenant: Api, tenantId: string, body: Record<string, unknown>): Promise<string> {
  const res = await tenant.call('POST', `/api/tenants/${tenantId}/backup-jobs`, body, [200, 201], { 'Idempotency-Key': randomUUID() })
  return res.id
}

export async function triggerAndWaitRun(tenant: Api, tenantId: string, jobId: string, timeoutMs = 90_000): Promise<{ runId: string; artifactId: string }> {
  const started = await tenant.call('POST', `/api/tenants/${tenantId}/backup-jobs/${jobId}/runs`, {}, [200, 201, 202], { 'Idempotency-Key': randomUUID() })
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const run = await tenant.call('GET', `/api/tenants/${tenantId}/backup-runs/${started.id}`)
    if (run.status === 'Succeeded') {
      const artifact = run.attempts.flatMap((a: any) => a.artifacts).at(-1)
      return { runId: run.id, artifactId: artifact.id }
    }
    if (['Failed', 'Cancelled', 'AgentLost'].includes(run.status)) throw new Error(`Seeded run ${started.id} ended ${run.status}: ${run.reasonCode}`)
    await sleep(2000)
  }
  throw new Error(`Seeded run ${started.id} timed out`)
}
