import { mockStorageProviders } from '@/lib/api/mock-data'
import type { StorageProvider } from '@/lib/types'

export function getStorageProviders(): Promise<readonly StorageProvider[]> {
  return Promise.resolve(mockStorageProviders)
}
