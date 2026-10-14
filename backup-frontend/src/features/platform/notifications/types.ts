export type EmailSecurity = 'starttls' | 'implicit' | 'none'
export type SmsAuthentication = 'none' | 'bearer' | 'basic' | 'header'

export interface EmailTransportInput {
  enabled: boolean
  host: string
  port: number
  security: EmailSecurity
  username: string | null
  fromAddress: string
  fromName: string | null
}

export interface EmailTransport {
  enabled: boolean
  configured: boolean
  host: string | null
  port: number | null
  security: EmailSecurity
  username: string | null
  fromAddress: string | null
  fromName: string | null
  hasSecret: boolean
  secretUpdatedAt: string | null
  updatedAt: string | null
}

export interface SmsTransportInput {
  enabled: boolean
  provider: 'generic-http'
  endpoint: string
  authentication: SmsAuthentication
  username: string | null
  headerName: string | null
  sender: string | null
}

export interface SmsTransport {
  enabled: boolean
  configured: boolean
  provider: 'generic-http'
  endpoint: string | null
  authentication: SmsAuthentication
  username: string | null
  headerName: string | null
  sender: string | null
  hasSecret: boolean
  secretUpdatedAt: string | null
  updatedAt: string | null
}

export interface NotificationTransports { email: EmailTransport; sms: SmsTransport }
