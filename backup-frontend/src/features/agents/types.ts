export interface AgentCapability {
  identifier: string
  support: string
  version: string | null
}

/** Safe tenant control-plane projection; no credentials or internal runtime identifiers. */
export interface Agent {
  agentId: string
  hostName: string | null
  status: string
  connectivity: 'online' | 'offline' | 'unknown'
  operationalStatus: string
  lastSeenAt: string | null
  lastHeartbeatAt: string | null
  lastConnectedAt: string | null
  platform: string
  agentVersion: string | null
  protocolVersion: string | null
  capabilityRevision: string | null
  capabilities: AgentCapability[]
}

export interface AgentDetail extends Agent {
  osDescription: string | null
  runtimeVersion: string | null
  enrolledAt: string | null
}

export interface EnrollmentPolicy {
  tenantId: string
  enabled: boolean
  changedAt?: string
}

export interface EnrollmentToken {
  tokenId: string
  createdAt: string
  expiresAt: string
  createdBy: string
}

export interface CreatedEnrollmentToken {
  tokenId: string
  token: string
  expiresAt: string
}
