import { readFileSync } from 'node:fs'

export type Account = { login: string; password: string }
export type State = {
  baseUrl: string
  apiUrl: string
  tenantId: string
  tenantName: string
  accounts: { platformAdmin: Account; userAdmin: Account }
}

export const state = (): State => JSON.parse(readFileSync(new URL('./.state.json', import.meta.url), 'utf8'))
