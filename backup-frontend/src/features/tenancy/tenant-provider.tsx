import { useEffect, useRef, type ReactNode } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { useAuth } from '@/features/auth/use-auth'
import { TenantContext } from './tenant-context'
import { tenantPermissions } from './permissions'

export function TenantProvider({ children }: { children: ReactNode }) {
  const { tenantId = '' } = useParams()
  const { tenants } = useAuth()
  const tenant = tenants.find(item => item.tenantId === tenantId)
  const cache = useQueryClient()
  const navigate = useNavigate()
  const previous = useRef(tenantId)
  useEffect(() => {
    const oldId = previous.current
    previous.current = tenantId
    if (oldId !== tenantId) {
      void cache.cancelQueries({ queryKey: ['tenant', oldId] })
      cache.removeQueries({ queryKey: ['tenant', oldId] })
    }
  }, [cache, tenantId])
  if (!tenant) return <div role="alert" className="p-6">Bu tenant için üyeliğiniz yok. <button type="button" className="text-primary underline" onClick={() => navigate('/')}>Tenant seçimine dön</button></div>
  async function switchTenant(id: string) {
    if (id === tenantId || !tenants.some(item => item.tenantId === id)) return
    await cache.cancelQueries({ queryKey: ['tenant', tenantId] })
    cache.removeQueries({ queryKey: ['tenant', tenantId] })
    navigate(`/tenants/${encodeURIComponent(id)}`)
  }
  return <TenantContext.Provider value={{ tenantId, tenant, ...tenantPermissions(tenant.membershipRole ?? tenant.role), switchTenant }}><div key={tenantId}>{children}</div></TenantContext.Provider>
}
