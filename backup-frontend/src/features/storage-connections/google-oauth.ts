/**
 * The callback page runs on the API origin (the redirect URI registered with Google), which differs from the frontend origin in local development.
 * Only a message from exactly that origin AND from the popup this call opened completes the flow; nothing else is trusted.
 */
function callbackOrigin(authorizationUrl: string): string | null {
  try {
    const redirect = new URL(authorizationUrl).searchParams.get('redirect_uri')
    return redirect ? new URL(redirect).origin : null
  } catch {
    return null
  }
}

export function openGoogleOAuthPopup(authorizationUrl: string, onCompleted: () => void) {
  const width = 600, height = 700
  const left = window.screenX + (window.outerWidth - width) / 2
  const top = window.screenY + (window.outerHeight - height) / 2
  const popup = window.open(authorizationUrl, 'google_oauth', `width=${width},height=${height},left=${left},top=${top}`)
  const expectedOrigin = callbackOrigin(authorizationUrl)
  const listener = (event: MessageEvent) => {
    if (event.data?.type !== 'GOOGLE_DRIVE_OAUTH_COMPLETED') return
    if (event.origin !== expectedOrigin || event.source !== popup) return
    window.removeEventListener('message', listener)
    popup?.close()
    onCompleted()
  }
  window.addEventListener('message', listener)
}
