export type UserRole = 'PlatformAdmin' | 'UserAdmin'
export interface UserProfile { userId: string; displayName: string; role: UserRole; isPlatformAdmin: boolean }
export interface TenantMembership { tenantId: string; name: string }
