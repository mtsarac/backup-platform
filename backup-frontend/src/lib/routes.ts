export const ROUTES = {
  LOGIN: '/login',
  DASHBOARD: '/',
} as const

export const connectionPath = (tenantId: string, id: string) => `/tenants/${tenantId}/storage/connections/${encodeURIComponent(id)}`
