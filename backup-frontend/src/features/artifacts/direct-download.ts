import { sha256 } from '@noble/hashes/sha2.js'
import { bytesToHex } from '@noble/hashes/utils.js'
import { apiRequest } from '@/lib/api/client'
import type { PreparedDownload } from './api'

export interface DownloadFile { createWritable(): Promise<FileSystemWritableFileStream> }
type PickerWindow = Window & { showSaveFilePicker?: (options: { suggestedName: string }) => Promise<DownloadFile> }

export function chooseDownloadFile(filename: string): Promise<DownloadFile> {
  const picker = (window as PickerWindow).showSaveFilePicker
  if (!picker || !window.isSecureContext || !window.RTCPeerConnection) return Promise.reject(new Error('DirectPathUnavailable'))
  // Must run in the save button's user activation, before any await or network call.
  return picker.call(window, { suggestedName: filename })
}

/** One awaited disk write per peer chunk. Closing the writable publishes only a fully verified file. */
export async function receiveDirectDownload(session: PreparedDownload, file: DownloadFile, signal: AbortSignal): Promise<void> {
  if (!/^\/api\/[a-zA-Z0-9/-]+\/content$/.test(session.downloadUrl)) throw new Error('DirectPathUnavailable')
  const path = session.downloadUrl.slice('/api/'.length)
  const peer = new RTCPeerConnection({ iceServers: [{ urls: 'stun:stun.cloudflare.com:3478' }], iceTransportPolicy: 'all' })
  const channel = peer.createDataChannel('backup-download-v1', { ordered: true })
  channel.binaryType = 'arraybuffer'
  let writer: FileSystemWritableFileStream
  try { writer = await file.createWritable() } catch (error) { peer.close(); throw error }
  const hash = sha256.create()
  let received = 0
  let expectedSize: number | undefined
  let published = false
  const { promise: saved, resolve, reject } = Promise.withResolvers<void>()
  const abort = () => reject(new Error('DirectPathUnavailable'))
  const deadline = window.setTimeout(abort, 15 * 60_000)
  signal.addEventListener('abort', abort, { once: true })
    let pending = Promise.resolve()
    channel.onmessage = event => {
      pending = pending.then(async () => {
        if (signal.aborted) throw new Error('DirectPathUnavailable')
        if (typeof event.data === 'string') {
          if (event.data === 'done') {
            if (expectedSize === undefined || received !== expectedSize || bytesToHex(hash.digest()) !== session.checksum) throw new Error('download_integrity_mismatch')
            await writer.close()
            published = true
            channel.send('saved')
            return
          }
          if (expectedSize !== undefined || event.data.length > 256) throw new Error('download_integrity_mismatch')
          const header = JSON.parse(event.data) as { sizeBytes: number; checksum: string }
          if (!Number.isSafeInteger(header.sizeBytes) || header.sizeBytes < 0 || header.checksum !== session.checksum || (!session.decrypted && header.sizeBytes !== session.sizeBytes)) throw new Error('download_integrity_mismatch')
          expectedSize = header.sizeBytes
          channel.send('ready')
        } else {
          if (!(event.data instanceof ArrayBuffer) || event.data.byteLength > 32 * 1024 || expectedSize === undefined || received + event.data.byteLength > expectedSize) throw new Error('download_integrity_mismatch')
          const chunk = new Uint8Array(event.data)
          hash.update(chunk)
          await writer.write(chunk)
          received += chunk.byteLength
          channel.send('ack')
        }
      }).catch(reject)
    }
    channel.onerror = abort
    channel.onclose = () => { if (published) resolve(); else abort() }
    peer.onconnectionstatechange = () => { if (['failed', 'closed', 'disconnected'].includes(peer.connectionState) && !published) abort() }
  // Attach immediately: a failed peer must not produce an unhandled rejection during signaling.
  void saved.catch(() => {})
  try {
    if (signal.aborted) throw new Error('DirectPathUnavailable')
    await peer.setLocalDescription(await peer.createOffer())
    const gathering = Promise.withResolvers<void>()
    peer.onicegatheringstatechange = () => { if (peer.iceGatheringState === 'complete') gathering.resolve() }
    if (peer.iceGatheringState === 'complete') gathering.resolve()
    const gatherTimeout = window.setTimeout(() => gathering.reject(new Error('DirectPathUnavailable')), 15_000)
    try { await gathering.promise } finally { window.clearTimeout(gatherTimeout) }
    await apiRequest(path, { method: 'POST', json: { sdp: peer.localDescription!.sdp }, signal })
    const until = Date.now() + 45_000
    let answer: string | null = null
    while (!answer && Date.now() < until) {
      const response = await apiRequest<{ sdp: string | null }>(path, { signal })
      answer = response.sdp
      if (!answer) {
        const delay = Promise.withResolvers<void>()
        window.setTimeout(delay.resolve, 500)
        await delay.promise
      }
    }
    if (!answer || answer.includes(' typ relay')) throw new Error('DirectPathUnavailable')
    await peer.setRemoteDescription({ type: 'answer', sdp: answer })
    await saved
  } finally {
    window.clearTimeout(deadline)
    signal.removeEventListener('abort', abort)
    peer.close()
    if (!published) await writer.abort().catch(() => {})
  }
}
