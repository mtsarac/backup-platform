import ky from 'ky'

export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5000/api'

export const api = ky.create({
  prefix: API_BASE_URL,
  headers: { Accept: 'application/json' },
  timeout: 30000,
})

export function setAuthHeader(token: string) {
  api.extend({
    headers: { Authorization: `Bearer ${token}` },
  })
}
