import { useMemo, useState, type ReactNode } from 'react'
import type { User } from '@/lib/types'
import { AuthContext } from './auth-context'

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)

  const value = useMemo(
    () => ({
      user,
      isAuthenticated: !!user,
      login: async (email: string) => {
        const normalizedEmail = email.trim().toLowerCase()
        if (normalizedEmail.includes('admin')) {
          setUser({ id: '1', username: 'admin', email: 'admin@example.com', role: 'administrator', status: 'active' })
        } else {
          const username = normalizedEmail.split('@')[0] || 'user'
          setUser({ id: 'user-1', username, email: normalizedEmail, role: 'operator', status: 'active' })
        }
      },
      logout: () => setUser(null),
    }),
    [user]
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
