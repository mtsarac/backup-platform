export function isWebAuthnSupported(): boolean {
  return typeof window !== 'undefined' && !!window.PublicKeyCredential && !!navigator.credentials
}

export function base64UrlToBuffer(base64url: string): ArrayBuffer {
  const padding = '='.repeat((4 - (base64url.length % 4)) % 4)
  const base64 = (base64url + padding).replace(/-/g, '+').replace(/_/g, '/')
  const raw = atob(base64)
  const buffer = new Uint8Array(raw.length)
  for (let i = 0; i < raw.length; i++) buffer[i] = raw.charCodeAt(i)
  return buffer.buffer
}

export function bufferToBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  let binary = ''
  for (let i = 0; i < bytes.byteLength; i++) binary += String.fromCharCode(bytes[i])
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export async function createCredential(options: {
  rp: { id?: string; name: string }
  user: { id: string; name: string; displayName: string }
  challenge: string
  pubKeyCredParams: { type: PublicKeyCredentialType; alg: number }[]
  timeout?: number
  excludeCredentials?: { id: string; type: PublicKeyCredentialType }[]
  authenticatorSelection?: AuthenticatorSelectionCriteria
  attestation?: AttestationConveyancePreference
}) {
  const publicKey: PublicKeyCredentialCreationOptions = {
    rp: options.rp,
    user: {
      ...options.user,
      id: base64UrlToBuffer(options.user.id)
    },
    challenge: base64UrlToBuffer(options.challenge),
    pubKeyCredParams: options.pubKeyCredParams,
    timeout: options.timeout,
    excludeCredentials: options.excludeCredentials?.map(c => ({
      ...c,
      id: base64UrlToBuffer(c.id)
    })),
    authenticatorSelection: options.authenticatorSelection,
    attestation: options.attestation
  }

  const credential = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null
  if (!credential) throw new Error('Authenticator did not return a credential.')

  const response = credential.response as AuthenticatorAttestationResponse
  return {
    id: credential.id,
    rawId: bufferToBase64Url(credential.rawId),
    type: credential.type,
    response: {
      attestationObject: bufferToBase64Url(response.attestationObject),
      clientDataJSON: bufferToBase64Url(response.clientDataJSON)
    }
  }
}

export async function getAssertion(options: {
  challenge: string
  timeout?: number
  rpId?: string
  allowCredentials?: { id: string; type: PublicKeyCredentialType }[]
  userVerification?: UserVerificationRequirement
}) {
  const publicKey: PublicKeyCredentialRequestOptions = {
    challenge: base64UrlToBuffer(options.challenge),
    timeout: options.timeout,
    rpId: options.rpId,
    allowCredentials: options.allowCredentials?.map(c => ({
      ...c,
      id: base64UrlToBuffer(c.id)
    })),
    userVerification: options.userVerification
  }

  const assertion = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null
  if (!assertion) throw new Error('Authenticator did not return an assertion.')

  const response = assertion.response as AuthenticatorAssertionResponse
  return {
    id: assertion.id,
    rawId: bufferToBase64Url(assertion.rawId),
    type: assertion.type,
    response: {
      authenticatorData: bufferToBase64Url(response.authenticatorData),
      clientDataJSON: bufferToBase64Url(response.clientDataJSON),
      signature: bufferToBase64Url(response.signature),
      userHandle: response.userHandle ? bufferToBase64Url(response.userHandle) : null
    }
  }
}
