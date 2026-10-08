export const platformPaths = {
  root: '/platform',
  tenants: '/platform/tenants',
  tenant: (tenantId: string) => `/platform/tenants/${encodeURIComponent(tenantId)}`,
  plans: '/platform/plans',
  users: '/platform/users',
  agents: (tenantId?: string) => tenantId ? `/platform/agents?tenantId=${encodeURIComponent(tenantId)}` : '/platform/agents',
  agent: (tenantId: string, agentId: string) => `/platform/tenants/${encodeURIComponent(tenantId)}/agents/${encodeURIComponent(agentId)}`,
  audit: (tenantId?: string) => tenantId ? `/platform/audit?tenantId=${encodeURIComponent(tenantId)}` : '/platform/audit',
} as const
