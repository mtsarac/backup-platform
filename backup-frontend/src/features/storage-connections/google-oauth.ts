const pollMs = 2_000
const giveUpMs = 10 * 60_000

/**
 * The callback page runs on the API origin (the redirect URI registered with Google), which differs from the frontend origin in local development.
 * Only a message from exactly that origin AND from the popup this call opened counts, and even then it is only a hint to check sooner.
 */
function callbackOrigin(authorizationUrl: string): string | null {
  try {
    const redirect = new URL(authorizationUrl).searchParams.get('redirect_uri')
    return redirect ? new URL(redirect).origin : null
  } catch {
    return null
  }
}

/**
 * Opens Google's consent popup and settles exactly once with whether the connection became connected.
 * The server state is the truth (a proxy can change the callback page's origin, so its message is unreliable): `isConnected` is polled while the popup is open,
 * once more when it closes, and the flow gives up after ten minutes. Returns a cancel function that stops everything without settling.
 */
export function openGoogleOAuthPopup(authorizationUrl: string, isConnected: () => Promise<boolean>, onSettled: (connected: boolean) => void): () => void {
  const width = 600, height = 700
  const left = window.screenX + (window.outerWidth - width) / 2
  const top = window.screenY + (window.outerHeight - height) / 2
  const popup = window.open(authorizationUrl, 'google_oauth', `width=${width},height=${height},left=${left},top=${top}`)
  const expectedOrigin = callbackOrigin(authorizationUrl)
  let done = false
  let checking = false

  const stop = () => {
    done = true
    clearInterval(timer)
    clearTimeout(giveUp)
    window.removeEventListener('message', listener)
  }
  async function check(final = false) {
    if (done || checking) return
    checking = true
    const connected = await isConnected().catch(() => false)
    checking = false
    if (done) return
    if (connected || final) {
      stop()
      popup?.close()
      onSettled(connected)
    }
  }
  const listener = (event: MessageEvent) => {
    if (event.data?.type !== 'GOOGLE_DRIVE_OAUTH_COMPLETED') return
    if (event.origin !== expectedOrigin || event.source !== popup) return
    void check()
  }
  // A blocked popup is null: it counts as already closed.
  const timer = setInterval(() => void check(popup?.closed ?? true), pollMs)
  const giveUp = setTimeout(() => { stop(); popup?.close(); onSettled(false) }, giveUpMs)
  window.addEventListener('message', listener)
  return stop
}
