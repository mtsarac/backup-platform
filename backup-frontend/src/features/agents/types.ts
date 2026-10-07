export interface AgentCapability {
  identifier: string
  support: string
  metadata?: Record<string, string> | null
  version?: string | null
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
  /** Revision the agent last advertised in a heartbeat. */
  capabilityRevision: string | null
  /** Revision of the persisted snapshot (null until the agent syncs). */
  capabilitiesRevision?: string | null
  capabilitiesReportedAt?: string | null
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

export interface EnrollmentTokenStatus {
  tokenId: string
  expiresAt: string
  state: 'Active' | 'Expired' | 'Consumed' | 'Revoked'
  agentId: string | null
}
