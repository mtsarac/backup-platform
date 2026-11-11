import { useLanguage } from '@/lib/i18n'
import { useEffect, useRef, type ReactNode } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useAuth } from '@/features/auth/use-auth'
import { TenantContext } from './tenant-context'
import { apiRequest } from '@/lib/api/client'
import type { TenantMembership } from '@/features/auth/types'
import { QueryState } from '@/components/data/query-state'
import { NotFound } from '@/components/layout/not-found'

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
    if (oldId && oldId !== tenantId) {
      void cache.cancelQueries({ queryKey: ['tenant', oldId] })
      cache.removeQueries({ queryKey: ['tenant', oldId] })
    }
  }, [cache, tenantId])
  if (!membership && user?.role === 'PlatformAdmin' && context.isFetching) {
    return <QueryState isPending error={null} />
  }
  if (!tenant) return <NotFound />
  async function switchTenant(id: string) {
    if (id === tenantId) return
    if (!tenants.some(item => item.tenantId === id) && user?.role !== 'PlatformAdmin') return
    navigate(`/tenants/${encodeURIComponent(id)}`)
  }
  return <TenantContext.Provider value={{ tenantId, tenant, canAdmin: true, canOperate: true, switchTenant }}><div key={tenantId}>{children}</div></TenantContext.Provider>
}
