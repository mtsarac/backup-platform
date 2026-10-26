export type BandwidthScope = 'tenant' | 'agent' | 'job'

/** Wire shape of GET upload-bandwidth: only agents/jobs that HAVE a limit are listed. Values are bytes per second (decimal). */
export interface UploadBandwidth {
  minimumBytesPerSecond: number
  maximumBytesPerSecond: number
  tenant: { bytesPerSecond: number | null; profileId?: string | null }
  agents: { agentId: string; bytesPerSecond: number; profileId?: string | null }[]
  jobs: { jobId: string; bytesPerSecond: number; profileId?: string | null }[]
}

export type BandwidthTarget = { scope: 'tenant' } | { scope: 'agent' | 'job'; id: string }

export interface TransferScheduleWindowDto {
  dayOfWeek: number
  startTime: string
  endTime: string
  bytesPerSecond: number
}

export interface BandwidthProfile {
  id: string
  name: string
  timeZoneId: string
  windows: TransferScheduleWindowDto[]
}
