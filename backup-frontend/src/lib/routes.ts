export const ROUTES = {
  LOGIN: '/login',
  DASHBOARD: '/',
} as const

export const connectionPath = (tenantId: string, id: string) => `/tenants/${tenantId}/storage/connections/${encodeURIComponent(id)}`
export const discoveryPath = (tenantId: string) => `/tenants/${tenantId}/discovery`
export const discoveryInstancePath = (tenantId: string, id: string) => `${discoveryPath(tenantId)}/instances/${encodeURIComponent(id)}`
