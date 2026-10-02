import { mockUsers } from '@/lib/api/mock-data'
import type { User } from '@/lib/types'

let users = [...mockUsers]

export async function getUsers(): Promise<User[]> {
  return Promise.resolve([...users])
}

export async function updateUserStatus(
  id: string,
  status: User['status'],
): Promise<User> {
  const user = users.find((u) => u.id === id)
  if (!user) throw new Error('User not found')
  user.status = status
  return Promise.resolve({ ...user })
}
