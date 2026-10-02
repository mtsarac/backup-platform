import { mockClients } from '@/lib/api/mock-data'
import type { Client } from '@/lib/types'

export function getClients(): Promise<readonly Client[]> {
  return Promise.resolve(mockClients)
}

export function getClient(id: string): Promise<Client | undefined> {
  return Promise.resolve(mockClients.find((client) => client.id === id))
}
