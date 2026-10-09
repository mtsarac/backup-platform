import { afterEach, describe, expect, it, vi } from 'vitest'
import { openGoogleOAuthPopup } from './google-oauth'

const authorizationUrl = `https://accounts.google.com/o/oauth2/v2/auth?redirect_uri=${encodeURIComponent('http://localhost:5252/api/platform/google-drive/oauth/callback')}`
const completed = { type: 'GOOGLE_DRIVE_OAUTH_COMPLETED', success: true }

function post(data: unknown, origin: string, source: unknown) {
  window.dispatchEvent(new MessageEvent('message', { data, origin, source: source as MessageEventSource }))
}

describe('openGoogleOAuthPopup', () => {
  afterEach(() => vi.restoreAllMocks())

  it('completes only for the popup it opened, from the callback origin', () => {
    const popup = { close: vi.fn() }
    vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window)
    const done = vi.fn()
    openGoogleOAuthPopup(authorizationUrl, done)

    post(completed, 'https://evil.example', popup)
    post(completed, 'http://localhost:5252', window)
    post({ type: 'other' }, 'http://localhost:5252', popup)
    expect(done).not.toHaveBeenCalled()

    post(completed, 'http://localhost:5252', popup)
    expect(done).toHaveBeenCalledTimes(1)
    expect(popup.close).toHaveBeenCalled()

    post(completed, 'http://localhost:5252', popup)
    expect(done).toHaveBeenCalledTimes(1)
  })
})
