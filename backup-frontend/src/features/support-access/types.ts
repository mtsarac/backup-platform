import type { SupportPermission, SupportStatus } from '@/features/platform/support/types'

export interface SupportAccessSummary {
  artifactsViewed: number
  downloadsStarted: number
  downloadsCompleted: number
  downloadsFailed: number
  restoresRequested: number
  restoresCancelled: number
}

export interface SupportAccessSession {
  id: string
  actorDisplayName: string | null
  reason: string
  permissions: SupportPermission[]
  startedAt: string
  expiresAt: string
  status: SupportStatus
  closedAt: string | null
  closeReason: string | null
  createdAt: string
  summary: SupportAccessSummary
}

export interface SupportAccessActivity {
  id: string
  at: string
  action: string
  targetType: string
  targetId: string
  result: string | null
}
