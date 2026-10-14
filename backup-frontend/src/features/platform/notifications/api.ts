import { apiRequest } from '@/lib/api/client'
import type { EmailTransport, EmailTransportInput, NotificationTransports, SmsTransport, SmsTransportInput } from './types'

const base = 'platform/notification-transports'

export function getTransports(signal?: AbortSignal) {
  return apiRequest<NotificationTransports>(base, { signal })
}

export function updateEmailTransport(input: EmailTransportInput) {
  return apiRequest<EmailTransport>(`${base}/email`, { method: 'PUT', json: input })
}

export function setEmailSecret(value: string) {
  return apiRequest<EmailTransport>(`${base}/email/secret`, { method: 'PUT', json: { value } })
}

export function clearEmailSecret() {
  return apiRequest<EmailTransport>(`${base}/email/secret`, { method: 'DELETE' })
}

export function updateSmsTransport(input: SmsTransportInput) {
  return apiRequest<SmsTransport>(`${base}/sms`, { method: 'PUT', json: input })
}

export function setSmsSecret(value: string) {
  return apiRequest<SmsTransport>(`${base}/sms/secret`, { method: 'PUT', json: { value } })
}

export function clearSmsSecret() {
  return apiRequest<SmsTransport>(`${base}/sms/secret`, { method: 'DELETE' })
}
