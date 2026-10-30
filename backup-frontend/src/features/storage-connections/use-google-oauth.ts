import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { getPlatformTenantConnections, getTenantConnections } from './api'
import { storageConnectionKeys, useGoogleDriveConnect, type ConnectionOps } from './hooks'
import { openGoogleOAuthPopup } from './google-oauth'
import type { ConnectionView } from './types'

export type GoogleOAuthOutcome = { status: 'idle' | 'waiting' | 'incomplete' } | { status: 'connected'; account: ConnectionView }

/**
 * Connected means a refresh token is stored and no re-authorization is pending. A successful consent always replaces the secret (a new `secretUpdatedAt`),
 * so comparing it with the value at the start also recognises re-authorizing a connection that already looked connected.
 */
const reconnected = (now: ConnectionView | undefined, before: ConnectionView) =>
  Boolean(now?.hasSecret && now.configuration?.reauthorizationRequired !== 'true' && now.secretUpdatedAt !== before.secretUpdatedAt)

/** The Google consent flow (`start(connection)`): opens the popup, follows the server until it is connected, then runs the connection test. */
export function useGoogleOAuth(ops: ConnectionOps) {
  const connect = useGoogleDriveConnect()
  const client = useQueryClient()
  const [outcome, setOutcome] = useState<GoogleOAuthOutcome>({ status: 'idle' })
  const cancel = useRef<() => void>(undefined)
  const latest = useRef<ConnectionView | undefined>(undefined)
  useEffect(() => () => cancel.current?.(), [])

  function start(before: ConnectionView) {
    cancel.current?.()
    const platform = ops.scope === 'platform'
    const read = () => client.fetchQuery({
      queryKey: platform ? storageConnectionKeys.platformTenantList(before.tenantId) : storageConnectionKeys.tenantList(before.tenantId),
      queryFn: ({ signal }) => (platform ? getPlatformTenantConnections : getTenantConnections)(before.tenantId, signal),
      staleTime: 0,
    }).then(({ items }) => {
      latest.current = items.find((item) => item.id === before.id)
      return reconnected(latest.current, before)
    })
    connect.mutate({ tenantId: before.tenantId, connectionId: before.id }, {
      onSuccess: ({ authorizationUrl }) => {
        setOutcome({ status: 'waiting' })
        cancel.current = openGoogleOAuthPopup(authorizationUrl, read, (connected) => {
          if (connected && latest.current) {
            setOutcome({ status: 'connected', account: latest.current })
            ops.test.mutate(before)
          } else setOutcome({ status: 'incomplete' })
        })
      },
    })
  }

  return { start, connect, outcome }
}
