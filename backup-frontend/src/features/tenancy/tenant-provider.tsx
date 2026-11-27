import { useLanguage } from '@/lib/i18n'
import { useEffect, useRef, type ReactNode } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { useAuth } from '@/features/auth/use-auth'
import { TenantContext } from './tenant-context'
import { NotFound } from '@/components/layout/not-found'

export function TenantProvider({ children }: { children: ReactNode }) {
  useLanguage()
  const { tenantId = '' } = useParams()
  const { user, tenants } = useAuth()
  const tenant = user?.role === 'PlatformAdmin' ? undefined : tenants.find(item => item.tenantId === tenantId)
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
  if (!tenant) return <NotFound standalone />
  async function switchTenant(id: string) {
    if (id === tenantId) return
    if (!tenants.some(item => item.tenantId === id)) return
    navigate(`/tenants/${encodeURIComponent(id)}`)
  }
  return <TenantContext.Provider value={{ tenantId, tenant, switchTenant }}><div key={tenantId}>{children}</div></TenantContext.Provider>
}
