import { tx } from '@/lib/i18n'

/** How a role reads on screen: a workspace user is just "Kullanıcı"; the raw role names (`UserAdmin`) never appear. */
export function accountRoleLabel(role: string): string {
  return role === 'PlatformAdmin' ? tx('Platform yöneticisi') : role === 'UserAdmin' ? tx('Kullanıcı') : role
}
