import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { openGoogleOAuthPopup } from './google-oauth'

const authorizationUrl = `https://accounts.google.com/o/oauth2/v2/auth?redirect_uri=${encodeURIComponent('http://localhost:5252/api/platform/google-drive/oauth/callback')}`
const completed = { type: 'GOOGLE_DRIVE_OAUTH_COMPLETED', success: true }

function post(data: unknown, origin: string, source: unknown) {
  window.dispatchEvent(new MessageEvent('message', { data, origin, source: source as MessageEventSource }))
}

function setup(connected: () => boolean = () => false) {
  const popup = { close: vi.fn(), closed: false }
  vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window)
  const isConnected = vi.fn(async () => connected())
  const settled = vi.fn()
  const cancel = openGoogleOAuthPopup(authorizationUrl, isConnected, settled)
  return { popup, isConnected, settled, cancel }
}

describe('openGoogleOAuthPopup', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks() })

  it('completes by polling when no message ever arrives', async () => {
    let connected = false
    const { popup, isConnected, settled } = setup(() => connected)
    await vi.advanceTimersByTimeAsync(2_000)
    expect(isConnected).toHaveBeenCalledTimes(1)
    expect(settled).not.toHaveBeenCalled()

    connected = true
    await vi.advanceTimersByTimeAsync(2_000)
    expect(settled).toHaveBeenCalledExactlyOnceWith(true)
    expect(popup.close).toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(10_000)
    expect(isConnected).toHaveBeenCalledTimes(2)
  })

  it('ignores a message from another origin, a stranger window or another type', async () => {
    const { popup, isConnected, settled } = setup()
    post(completed, 'https://localhost:5252', popup)
    post(completed, 'https://evil.example', popup)
    post(completed, 'http://localhost:5252', window)
    post({ type: 'other' }, 'http://localhost:5252', popup)
    await vi.advanceTimersByTimeAsync(0)
    expect(isConnected).not.toHaveBeenCalled()
    expect(settled).not.toHaveBeenCalled()
  })

  it('treats a message from the callback origin and popup as a hint that checks at once', async () => {
    const { popup, isConnected, settled } = setup(() => true)
    post(completed, 'http://localhost:5252', popup)
    await vi.advanceTimersByTimeAsync(0)
    expect(isConnected).toHaveBeenCalledTimes(1)
    expect(settled).toHaveBeenCalledExactlyOnceWith(true)
  })

  it('checks once more when the popup closes and reports the outcome', async () => {
    const closedAfterConnect = setup(() => true)
    closedAfterConnect.popup.closed = true
    await vi.advanceTimersByTimeAsync(2_000)
    expect(closedAfterConnect.settled).toHaveBeenCalledExactlyOnceWith(true)
    vi.restoreAllMocks()

    const closedWithout = setup(() => false)
    closedWithout.popup.closed = true
    await vi.advanceTimersByTimeAsync(2_000)
    expect(closedWithout.isConnected).toHaveBeenCalledTimes(1)
    expect(closedWithout.settled).toHaveBeenCalledExactlyOnceWith(false)
    await vi.advanceTimersByTimeAsync(10_000)
    expect(closedWithout.isConnected).toHaveBeenCalledTimes(1)
  })

  it('keeps polling through a failed check', async () => {
    const { isConnected, settled } = setup()
    isConnected.mockRejectedValueOnce(new Error('offline'))
    await vi.advanceTimersByTimeAsync(2_000)
    expect(settled).not.toHaveBeenCalled()
    isConnected.mockResolvedValueOnce(true)
    await vi.advanceTimersByTimeAsync(2_000)
    expect(settled).toHaveBeenCalledExactlyOnceWith(true)
  })

  it('gives up after ten minutes', async () => {
    const { isConnected, settled } = setup()
    await vi.advanceTimersByTimeAsync(10 * 60_000)
    expect(settled).toHaveBeenCalledExactlyOnceWith(false)
    const calls = isConnected.mock.calls.length
    await vi.advanceTimersByTimeAsync(60_000)
    expect(isConnected).toHaveBeenCalledTimes(calls)
  })

  it('stops everything when cancelled', async () => {
    const { popup, isConnected, settled, cancel } = setup(() => true)
    cancel()
    post(completed, 'http://localhost:5252', popup)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(isConnected).not.toHaveBeenCalled()
    expect(settled).not.toHaveBeenCalled()
  })
})
