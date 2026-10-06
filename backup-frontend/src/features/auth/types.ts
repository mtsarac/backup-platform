export type TenantRole = 'Owner' | 'Admin' | 'Administrator' | 'Operator' | 'Viewer'
export interface UserProfile { userId: string; displayName: string; isPlatformAdmin: boolean }
export interface TenantMembership { tenantId: string; name: string; role: TenantRole; membershipRole?: TenantRole }
