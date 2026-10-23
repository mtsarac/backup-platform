import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { sha256 } from '@noble/hashes/sha2.js'
import { bytesToHex } from '@noble/hashes/utils.js'
import { apiRequest } from '@/lib/api/client'
import { chooseDownloadFile, receiveDirectDownload } from './direct-download'

vi.mock('@/lib/api/client', () => ({ apiRequest: vi.fn() }))

class FakePeer {
  static current: FakePeer
  iceGatheringState = 'complete'
  localDescription = { sdp: 'offer' }
  connectionState = 'connected'
  channel = { binaryType: '', onmessage: null as ((event: { data: unknown }) => void) | null, onerror: null, onclose: null as (() => void) | null, send: vi.fn(), close: vi.fn() }
  onconnectionstatechange: (() => void) | null = null
  onicegatheringstatechange: (() => void) | null = null
  config: RTCConfiguration
  constructor(config: RTCConfiguration) { this.config = config; FakePeer.current = this }
  createDataChannel() { return this.channel }
  async createOffer() { return { type: 'offer', sdp: 'offer' } }
  async setLocalDescription() {}
  async setRemoteDescription() {}
  close = vi.fn()
}
const bytes = new Uint8Array([1, 2, 3, 4])
const session = { sessionId: 'session', downloadUrl: '/api/tenants/tenant/backup-artifacts/artifact/downloads/session/content', filename: 'backup.bak', checksum: bytesToHex(sha256(bytes)), sizeBytes: bytes.length, decrypted: false, direct: true }

beforeEach(() => {
  vi.stubGlobal('RTCPeerConnection', FakePeer)
  vi.mocked(apiRequest).mockResolvedValue({ sdp: 'answer' })
})
afterEach(() => { vi.unstubAllGlobals(); vi.clearAllMocks() })

async function connected() {
  await vi.waitFor(() => expect(apiRequest).toHaveBeenCalledTimes(2))
  return FakePeer.current.channel
}

describe('direct browser disk publication', () => {
  it('awaits disk writes before acknowledging and publishes only after length and incremental hash match', async () => {
    const disk = Promise.withResolvers<void>()
    const writer = { write: vi.fn(() => disk.promise), close: vi.fn(async () => {}), abort: vi.fn(async () => {}) }
    const run = receiveDirectDownload(session, { createWritable: async () => writer as unknown as FileSystemWritableFileStream }, new AbortController().signal)
    const channel = await connected()
    channel.onmessage!({ data: JSON.stringify({ sizeBytes: bytes.length, checksum: session.checksum }) })
    await vi.waitFor(() => expect(channel.send).toHaveBeenCalledWith('ready'))
    channel.onmessage!({ data: bytes.buffer })
    await vi.waitFor(() => expect(writer.write).toHaveBeenCalledTimes(1))
    expect(channel.send).not.toHaveBeenCalledWith('ack')
    expect(writer.close).not.toHaveBeenCalled()
    disk.resolve()
    await vi.waitFor(() => expect(channel.send).toHaveBeenCalledWith('ack'))
    channel.onmessage!({ data: 'done' })
    await vi.waitFor(() => expect(channel.send).toHaveBeenCalledWith('saved'))
    channel.onclose!()
    await run
    expect(writer.close).toHaveBeenCalledTimes(1)
    expect(writer.abort).not.toHaveBeenCalled()
    expect(FakePeer.current.config.iceServers?.every(server => !JSON.stringify(server).includes('turn:'))).toBe(true)
    expect(vi.mocked(apiRequest).mock.calls.every(([, options]) => !JSON.stringify(options).includes('AQIDBA=='))).toBe(true)
  })

  it('aborts unverified output rather than publishing corrupt content or retrying through backend', async () => {
    const writer = { write: vi.fn(async () => {}), close: vi.fn(async () => {}), abort: vi.fn(async () => {}) }
    const run = receiveDirectDownload(session, { createWritable: async () => writer as unknown as FileSystemWritableFileStream }, new AbortController().signal)
    const failure = expect(run).rejects.toThrow('download_integrity_mismatch')
    const channel = await connected()
    channel.onmessage!({ data: JSON.stringify({ sizeBytes: bytes.length, checksum: session.checksum }) })
    channel.onmessage!({ data: new Uint8Array([4, 3, 2, 1]).buffer })
    channel.onmessage!({ data: 'done' })
    await failure
    expect(writer.abort).toHaveBeenCalledTimes(1)
    expect(writer.close).not.toHaveBeenCalled()
    expect(apiRequest).toHaveBeenCalledTimes(2)
    expect(FakePeer.current.close).toHaveBeenCalledTimes(1)
  })

  it('rejects unsafe signaling URLs before any disk or backend operation', async () => {
    const createWritable = vi.fn()
    await expect(receiveDirectDownload({ ...session, downloadUrl: 'https://attacker.test/content' }, { createWritable }, new AbortController().signal)).rejects.toThrow('DirectPathUnavailable')
    expect(createWritable).not.toHaveBeenCalled()
    expect(apiRequest).not.toHaveBeenCalled()
  })

  it('explicitly refuses browsers without the secure user-selected disk API', async () => {
    vi.stubGlobal('isSecureContext', true)
    await expect(chooseDownloadFile('backup.bak')).rejects.toThrow('DirectPathUnavailable')
    expect(apiRequest).not.toHaveBeenCalled()
  })
})
