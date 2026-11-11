import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useRef, type ReactNode } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useAuth } from '@/features/auth/use-auth'
import { TenantContext } from './tenant-context'
import { Button } from '@/components/ui/button'
import { apiRequest } from '@/lib/api/client'
import type { TenantMembership } from '@/features/auth/types'
import { QueryState } from '@/components/data/query-state'

export function TenantProvider({ children }: { children: ReactNode }) {
  useLanguage()
  const { tenantId = '' } = useParams()
  const { user, tenants } = useAuth()
  const membership = tenants.find(item => item.tenantId === tenantId)
  const context = useQuery({ queryKey: ['tenant', tenantId, 'context'], queryFn: ({ signal }) => apiRequest<TenantMembership>(`tenants/${encodeURIComponent(tenantId)}/context`, { signal }), enabled: user?.role === 'PlatformAdmin' && !membership && !!tenantId, retry: false })
  const tenant = membership ?? context.data
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
  if (!membership && user?.role === 'PlatformAdmin' && (context.isFetching || context.error)) return <QueryState isPending={context.isFetching} error={context.error} forbiddenMessage={() => tx("Bu tenant için üyeliğiniz yok.")} />
  if (!tenant) return <div role="alert" className="p-6">{tx("Bu tenant için üyeliğiniz yok.")}{' '}<Button variant="link" size="sm" type="button" onClick={() => navigate('/')}>{tx("Tenant seçimine dön")}</Button></div>
  async function switchTenant(id: string) {
    if (id === tenantId || !tenants.some(item => item.tenantId === id)) return
    navigate(`/tenants/${encodeURIComponent(id)}`)
  }
  return <TenantContext.Provider value={{ tenantId, tenant, canAdmin: true, canOperate: true, switchTenant }}><div key={tenantId}>{children}</div></TenantContext.Provider>
}
