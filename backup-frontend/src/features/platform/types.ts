import type { AgentCapability } from '@/features/agents/types'

export type MembershipRole = 'Owner' | 'Admin' | 'Operator' | 'Viewer'
export const membershipRoles: readonly MembershipRole[] = ['Owner', 'Admin', 'Operator', 'Viewer']

export interface PageParams {
  page?: number
  pageSize?: number
}

export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
}

export interface AgentCounts {
  total: number
  online: number
  offline: number
  unknown: number
  disabled: number
  revoked: number
}

export interface PlatformSummary {
  tenants: number
  users: number
  agents: AgentCounts
  enabledBackupJobs: number
  failedRunsLast24Hours: number
}

export interface PlatformTenant {
  tenantId: string
  name: string
  memberCount: number
  agentCount: number
  enrollmentEnabled: boolean
}

export interface PlatformTenantDetail {
  tenantId: string
  name: string
  memberCount: number
  enabledBackupJobs: number
  agents: AgentCounts
  enrollment: { enabled: boolean; changedAt: string | null }
}

export interface TenantEnrollmentResult {
  tenantId: string
  enabled: boolean
  changed: boolean
}

export interface PlatformMembership {
  userId: string
  displayName: string
  loginIdentifier: string | null
  role: MembershipRole
}

export interface PlatformUser {
  userId: string
  displayName: string
  loginIdentifier: string | null
  isPlatformAdmin: boolean
  membershipCount: number
}

export interface CreateUserInput {
  loginIdentifier: string
  displayName: string
  password: string
}

export type AgentConnectivity = 'online' | 'offline' | 'unknown'
export type AgentLifecycle = 'PendingEnrollment' | 'Active' | 'Disabled' | 'Revoked'

export interface PlatformAgent {
  tenantId: string
  tenantName: string
  agentId: string
  hostName: string | null
  status: string
  connectivity: AgentConnectivity
  operationalStatus: string
  platform: string
  osDescription: string | null
  agentVersion: string | null
  protocolVersion: string | null
  capabilityCount: number
  capabilityRevision: string | null
  capabilitiesReportedAt: string | null
  lastSeenAt: string | null
  enrolledAt: string | null
}

export interface PlatformAgentDetail extends PlatformAgent {
  lastHeartbeatAt: string | null
  lastConnectedAt: string | null
  runtimeVersion: string | null
  capabilities: AgentCapability[]
}

export interface PlatformAgentFilters extends PageParams {
  tenantId?: string
  connectivity?: AgentConnectivity
  status?: AgentLifecycle
  search?: string
}

export interface SearchParams extends PageParams {
  search?: string
}

export type AuditDetailValue = string | number | boolean | null

export interface PlatformAuditEntry {
  id: string
  at: string
  actorUserId: string
  actorDisplayName: string | null
  actorKind: 'PlatformAdmin' | 'TenantAdministrator'
  action: string
  tenantId: string | null
  tenantName: string | null
  targetType: string
  targetId: string
  details: Record<string, AuditDetailValue>
}

export interface AuditFilters extends PageParams {
  tenantId?: string
}
