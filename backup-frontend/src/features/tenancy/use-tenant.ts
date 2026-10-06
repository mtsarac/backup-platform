import { useContext } from 'react'
import { TenantContext } from './tenant-context'

export function useTenant() {
  const context = useContext(TenantContext)
  if (!context) throw new Error('Tenant context required')
  return context
}
