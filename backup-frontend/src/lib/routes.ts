export const ROUTES = {
  LOGIN: '/login',
  DASHBOARD: '/',
} as const

export const connectionPath = (tenantId: string, id: string) => `/tenants/${tenantId}/storage/connections/${encodeURIComponent(id)}`
export const restorePath = (tenantId: string, id: string) => `/tenants/${tenantId}/restores/${encodeURIComponent(id)}`
export const discoveredDatabasesPath = (tenantId: string) => `/tenants/${tenantId}/agents/databases`
export const discoveryInstancePath = (tenantId: string, id: string) => `/tenants/${tenantId}/discovery/instances/${encodeURIComponent(id)}`
