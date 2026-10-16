import { tx } from '@/lib/i18n'
import { useContext } from 'react'
import { TenantContext } from './tenant-context'

export function useTenant() {
  const context = useContext(TenantContext)
  if (!context) throw new Error(tx('Tenant context required'))
  return context
}
