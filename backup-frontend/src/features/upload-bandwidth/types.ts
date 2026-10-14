export type BandwidthScope = 'tenant' | 'agent' | 'job'

/** Wire shape of GET upload-bandwidth: only agents/jobs that HAVE a limit are listed. Values are bytes per second (decimal). */
export interface UploadBandwidth {
  minimumBytesPerSecond: number
  maximumBytesPerSecond: number
  tenant: { bytesPerSecond: number | null }
  agents: { agentId: string; bytesPerSecond: number }[]
  jobs: { jobId: string; bytesPerSecond: number }[]
}

export type BandwidthTarget = { scope: 'tenant' } | { scope: 'agent' | 'job'; id: string }
